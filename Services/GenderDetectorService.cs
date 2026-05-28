using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace HrChatThaiLLM.Server.Services;

public enum GenderPreference
{
    Neutral = 0,
    Male = 1,
    Female = 2,
    LGBT = 3
}

public interface IGenderDetectorService
{
    GenderPreference? DetectFromMessage(string message);
    bool TryHandleOverrideCommand(string message, out GenderPreference? preference, out bool resetRequested);
}

public class GenderDetectorService : IGenderDetectorService
{
    private static readonly Dictionary<string, GenderPreference> FallbackParticleMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ครับ"] = GenderPreference.Male,
        ["คับ"] = GenderPreference.Male,
        ["ฮะ"] = GenderPreference.Male,
        ["คร้าบ"] = GenderPreference.Male,
        ["ครัช"] = GenderPreference.Male,
        ["ครับผม"] = GenderPreference.Male,
        ["ค่ะ"] = GenderPreference.Female,
        ["คะ"] = GenderPreference.Female,
        ["นะคะ"] = GenderPreference.Female,
        ["ค้า"] = GenderPreference.Female,
        ["ขา"] = GenderPreference.Female,
        ["อ่ะ"] = GenderPreference.LGBT,
        ["จ้า"] = GenderPreference.LGBT,
        ["ตัวเอง"] = GenderPreference.LGBT,
        ["นะแก"] = GenderPreference.LGBT,
        ["จ่ะ"] = GenderPreference.LGBT
    };
    private readonly string _connectionString;
    private readonly ILogger<GenderDetectorService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, GenderPreference> _particleMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public GenderDetectorService(IConfiguration configuration, ILogger<GenderDetectorService> logger)
    {
        _logger = logger;
        _connectionString = configuration.GetConnectionString("ChatHistoryDatabase")
            ?? throw new InvalidOperationException("Connection string 'ChatHistoryDatabase' not found.");
    }

    public GenderPreference? DetectFromMessage(string message)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(message)) return null;

        var words = Regex.Split(message.Trim(), @"\s+")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeToken)
            .ToArray();

        for (var i = words.Length - 1; i >= 0; i--)
        {
            var token = words[i];
            if (_particleMap.TryGetValue(token, out var pref))
                return pref;
        }

        foreach (var (particle, pref) in _particleMap)
        {
            if (message.Contains(particle, StringComparison.OrdinalIgnoreCase))
                return pref;
        }

        return null;
    }

    public bool TryHandleOverrideCommand(string message, out GenderPreference? preference, out bool resetRequested)
    {
        preference = null;
        resetRequested = false;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var msg = message.Trim();
        if (msg.Contains("ลืมคำลงท้าย", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("รีเซ็ตคำลงท้าย", StringComparison.OrdinalIgnoreCase))
        {
            resetRequested = true;
            return true;
        }

        if (msg.Contains("เปลี่ยนคำลงท้ายเป็นผู้หญิง", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("ตั้งคำลงท้ายเป็นผู้หญิง", StringComparison.OrdinalIgnoreCase))
        {
            preference = GenderPreference.Female;
            return true;
        }

        if (msg.Contains("เปลี่ยนคำลงท้ายเป็นผู้ชาย", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("ตั้งคำลงท้ายเป็นผู้ชาย", StringComparison.OrdinalIgnoreCase))
        {
            preference = GenderPreference.Male;
            return true;
        }

        if (msg.Contains("เปลี่ยนคำลงท้ายเป็นกลาง", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("ตั้งคำลงท้ายเป็นกลาง", StringComparison.OrdinalIgnoreCase))
        {
            preference = GenderPreference.Neutral;
            return true;
        }

        return false;
    }

    private static string NormalizeToken(string value)
    {
        return value.Trim().Trim('.', ',', '!', '?', 'ๆ', '…', '(', ')', '[', ']', '"', '\'');
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _lock.Wait();
        try
        {
            if (_loaded) return;
            LoadParticleMap();
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadParticleMap()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = """
                SELECT Particle, GenderPreference
                FROM ParticleGenderMap
                WHERE IsActive = 1
                """;
            var rows = conn.Query<ParticleMapRow>(sql).ToList();

            var loaded = new Dictionary<string, GenderPreference>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Particle)) continue;
                if (!TryParsePreference(row.GenderPreference, out var pref)) continue;
                loaded[NormalizeToken(row.Particle)] = pref;
            }

            if (loaded.Count == 0)
            {
                _particleMap = new Dictionary<string, GenderPreference>(FallbackParticleMap, StringComparer.OrdinalIgnoreCase);
                _logger.LogWarning("ParticleGenderMap loaded 0 rows. Using fallback particle map.");
                return;
            }

            _particleMap = loaded;
            _logger.LogInformation("GenderDetector loaded {Count} particles from ParticleGenderMap.", loaded.Count);
        }
        catch (Exception ex)
        {
            _particleMap = new Dictionary<string, GenderPreference>(FallbackParticleMap, StringComparer.OrdinalIgnoreCase);
            _logger.LogError(ex, "Failed loading ParticleGenderMap. Fallback map will be used.");
        }
    }

    private static bool TryParsePreference(string? raw, out GenderPreference preference)
    {
        preference = GenderPreference.Neutral;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();
        if (value.Equals("Male", StringComparison.OrdinalIgnoreCase))
        {
            preference = GenderPreference.Male;
            return true;
        }
        if (value.Equals("Female", StringComparison.OrdinalIgnoreCase))
        {
            preference = GenderPreference.Female;
            return true;
        }
        if (value.Equals("LGBT", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("LGBTQ", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("LGBTQ+", StringComparison.OrdinalIgnoreCase))
        {
            preference = GenderPreference.LGBT;
            return true;
        }
        if (value.Equals("Neutral", StringComparison.OrdinalIgnoreCase))
        {
            preference = GenderPreference.Neutral;
            return true;
        }
        return false;
    }

    private sealed class ParticleMapRow
    {
        public string Particle { get; set; } = "";
        public string GenderPreference { get; set; } = "";
    }
}
