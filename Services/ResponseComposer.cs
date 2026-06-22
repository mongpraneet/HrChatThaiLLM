using Dapper;
using Microsoft.Data.SqlClient;
using HrChatThaiLLM.Server.Models;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using HrChatThaiLLM.Server.Services;

namespace HrChatThaiLLM.Server.Services;

public interface IResponseComposer
{
    Task RefreshCacheAsync();
    string? CheckProfanity(string message, string gender, string employeeName);
    string ComposeResponse(string pluginKey, string bodyContent, string employeeName, string gender, DateTime? lastChatTime = null);

    // ✅ Interface สำหรับฟังก์ชันสรุป
    string ComposeSummaryResponse(List<ChatSummaryItem> items, string gender, string employeeName);
}

public class ResponseComposer : IResponseComposer
{
    private readonly string _connectionString;
    private readonly ILogger<ResponseComposer> _logger;
    // ไม่ต้อง Inject IChatSummaryService ใน Constructor ของ Composer ถ้าไม่จำเป็นต้องเข้าถึง Cache โดยตรงจากที่นี่
    // เพราะ List<ChatSummaryItem> ถูกส่งเข้ามาเป็น Argument แล้ว ทำให้ Class นี้ Loose Coupling มากขึ้น
    // แต่ถ้าต้องการใช้เพื่อเหตุผลอื่น สามารถเก็บไว้ได้

    private static readonly ConcurrentBag<string> _profanityList = new();
    private static readonly ConcurrentDictionary<string, int> _pluginMap = new();
    private static readonly ConcurrentDictionary<int, List<ResponseTemplateRow>> _templateMap = new();
    private static readonly List<string> _profanityWarnings = new();

    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static bool _isInitialized = false;

    public ResponseComposer(IConfiguration config, ILogger<ResponseComposer> logger)
    {
        _connectionString = config.GetConnectionString("ChatHistoryDatabase")
            ?? throw new InvalidOperationException("ChatHistoryDatabase string not found");
        _logger = logger;
        // ลบ _summaryService ออกจาก Constructor เพราะไม่จำเป็นต้องใช้ในคลาสนี้โดยตรง
        // ข้อมูลถูกส่งเข้ามาผ่านพารามิเตอร์ของเมธอดแล้ว
    }

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized) return;
        await _lock.WaitAsync();
        try
        {
            if (!_isInitialized)
            {
                await RefreshCacheAsync();
                _isInitialized = true;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RefreshCacheAsync()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            _logger.LogInformation("🔄 ResponseComposer: โหลดและจัดการข้อมูลระบบขึ้นหน่วยความจำ...");

            var badWords = await conn.QueryAsync<string>("SELECT BadWord FROM ProfanityBlacklist WHERE IsActive = 1");
            _profanityList.Clear();
            foreach (var word in badWords) _profanityList.Add(word.Trim());

            var plugins = await conn.QueryAsync<PluginRow>("SELECT Id, PluginKey FROM Plugins");
            _pluginMap.Clear();
            foreach (var p in plugins) _pluginMap.TryAdd(p.PluginKey, p.Id);

            var templates = await conn.QueryAsync<ResponseTemplateRow>("SELECT Id, PluginId, Section, TemplateText FROM ResponseTemplates WHERE IsActive = 1");

            _templateMap.Clear();
            _profanityWarnings.Clear();

            foreach (var t in templates)
            {
                if (t.Section == "PROFANITY_WARNING")
                {
                    _profanityWarnings.Add(t.TemplateText);
                    continue;
                }

                if (t.PluginId.HasValue)
                {
                    if (!_templateMap.ContainsKey(t.PluginId.Value))
                    {
                        _templateMap[t.PluginId.Value] = new List<ResponseTemplateRow>();
                    }
                    _templateMap[t.PluginId.Value].Add(t);
                }
            }
            _logger.LogInformation("✅ ResponseComposer: แคชข้อมูลเรียบร้อยแล้ว (คำหยาบ: {Count0}, เทมเพลต: {Count1})", _profanityList.Count, templates.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ โหลดข้อมูลล้มเหลวใน ResponseComposer");
        }
    }

    public string? CheckProfanity(string message, string gender, string employeeName)
    {
        // ใช้ GetAwaiter().GetResult() เฉพาะเมื่อจำเป็นจริงๆ ในบริบท Sync
        EnsureInitializedAsync().GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(message)) return null;

        bool hasBadWord = _profanityList.Any(badWord => message.Contains(badWord, StringComparison.OrdinalIgnoreCase));

        if (hasBadWord && _profanityWarnings.Count > 0)
        {
            string politeWord = IsFemaleGender(gender) ? "ค่ะ" : "ครับ";
            string fullNameWithKhun = $"คุณ {employeeName}";

            int randomIndex = Random.Shared.Next(_profanityWarnings.Count);
            string templateText = _profanityWarnings[randomIndex];

            try
            {
                return string.Format(templateText, politeWord, fullNameWithKhun);
            }
            catch (FormatException)
            {
                return templateText.Replace("{0}", politeWord).Replace("{1}", fullNameWithKhun);
            }
        }

        return null;
    }

    public string ComposeResponse(string pluginKey, string bodyContent, string employeeName, string gender, DateTime? lastChatTime = null)
    {
        EnsureInitializedAsync().GetAwaiter().GetResult();

        bool isFollowUp = lastChatTime.HasValue && (DateTime.Now - lastChatTime.Value).TotalMinutes < 3;

        var (greeting, closing) = GetGreetingAndClosing(pluginKey, gender, employeeName, isFollowUp);

        return $"""
            {greeting}

            {bodyContent.Trim()}

            {closing}
            """;
    }

    // ✅ Optimized: ฟังก์ชันสร้างข้อความสรุปประวัติการแชท
    // ไฟล์: ResponseComposer.cs

    public string ComposeSummaryResponse(List<ChatSummaryItem> items, string gender, string employeeName)
    {
        string politeWord = IsFemaleGender(gender) ? "ค่ะ" : "ครับ";
        string fullNameWithKhun = $"คุณ {employeeName}";

        if (items == null || !items.Any())
        {
            // 🎲 สุ่มคำปฏิเสธเมื่อไม่มีประวัติ (เนียนตาขึ้นมาก)
            string[] emptyFormats = new[] {
            $"ยังไม่มีประวัติการสอบถามให้สรุปเลย{politeWord} {fullNameWithKhun} ลองเริ่มพิมพ์ถามข้อมูลกับผมได้เลยนะ{politeWord}",
            $"ณ เซสชันนี้ หนูนึกประวัติการคุยไม่ออกเลยค่ะ {fullNameWithKhun} เพราะเรายังไม่ได้เริ่มถามคำถามด้าน HR กันเลย",
            $"ข้อมูลสรุปยังว่างเปล่าอยู่{politeWord} หากต้องการใช้งานฟังก์ชันนี้ โปรดพิมพ์ถามเรื่องวันลาหรือสิทธิ์ค่ารักษาพยาบาลก่อนนะ{politeWord}"
        };
            return emptyFormats[Random.Shared.Next(emptyFormats.Length)];
        }

        var (greeting, closing) = GetGreetingAndClosing("Fallback", gender, employeeName, false);
        var sb = new StringBuilder();

        // 🎲 สุ่มหัวข้อรายงานประวัติแชทเลียนแบบสไตล์ Generative AI
        string[] summaryHeaders = new[] {
        $"📋 **สรุปบันทึก Insight การสนทนาของ{fullNameWithKhun}**\n",
        $"🧠 **วิเคราะห์ประเด็น HR ที่สอบถามเข้ามาล่าสุด**\n",
        $"🗂️ **แฟ้มรายงานสรุปหัวข้อความสนใจของคุณในวันนี้**\n"
    };

        sb.AppendLine(summaryHeaders[Random.Shared.Next(summaryHeaders.Length)]);

        foreach (var item in items.OrderByDescending(i => i.Timestamp))
        {
            sb.AppendLine($"🔹 **หัวข้อเรื่อง: {item.Topic}**");
            if (item.AskCount > 1)
            {
                // สุ่มข้อความเตือนการถามซ้ำแบบเอ็นดูพนักงาน
                string[] duplicateAlerts = new[] {
                $"*(หัวข้อนี้ถามย้ำเข้ามาแล้ว {item.AskCount} ครั้งนะ{politeWord})*",
                $"_({fullNameWithKhun} สนใจจุดนี้เป็นพิเศษ ถามซ้ำมา {item.AskCount} รอบแล้วค่ะ)_",
                $"*[พนักงานให้ความสำคัญ] ยื่นถามประเด็นนี้ {item.AskCount} ครั้ง*"
            };
                sb.AppendLine(duplicateAlerts[Random.Shared.Next(duplicateAlerts.Length)]);
            }
            sb.AppendLine(item.KeyInfo);
            sb.AppendLine("-------------------------");
        }

        sb.AppendLine($"\n{closing}");
        return sb.ToString();
    }

    // ✅ Helper Method: แยก Logic การสุ่มคำทักทายและปิดท้ายออกมาเพื่อลด Code Duplication
    private (string Greeting, string Closing) GetGreetingAndClosing(string pluginKey, string gender, string employeeName, bool isFollowUp)
    {
        EnsureInitializedAsync().GetAwaiter().GetResult();

        string politeWord = IsFemaleGender(gender) ? "ค่ะ" : "ครับ";
        string fullNameWithKhun = $"คุณ{employeeName}"; // ไม่มีช่องว่างเพื่อให้ต่อกันสนิท
        
        int hour = DateTime.Now.Hour;
        string timeGreeting = "";
        if (hour >= 5 && hour < 12) timeGreeting = "อรุณสวัสดิ์ยามเช้า";
        else if (hour >= 12 && hour < 17) timeGreeting = "สวัสดีตอนบ่าย";
        else timeGreeting = "สวัสดีตอนเย็น";

        var placeholders = new Dictionary<string, string>
        {
            { "Polite", politeWord },
            { "KhunName", fullNameWithKhun },
            { "TimeGreeting", timeGreeting },
            { "CurrentTime", DateTime.Now.ToString("HH:mm") },
            { "Date", DateTime.Now.ToString("dd/MM/yyyy") },
            { "0", politeWord }, // Backward compatibility
            { "1", fullNameWithKhun } // Backward compatibility
        };

        string greeting = $"สวัสดี{politeWord}";
        string closing = $"ยินดีที่ได้ช่วยเหลือ{politeWord}";

        bool found = false;
        if (!string.IsNullOrEmpty(pluginKey) && _pluginMap.TryGetValue(pluginKey.ToLowerInvariant(), out int pluginId) && _templateMap.TryGetValue(pluginId, out var templates))
        {
            found = true;
            ProcessTemplates(templates, placeholders, isFollowUp, ref greeting, ref closing);
        }

        // Fallback ถ้าไม่พบ PluginKey ที่ระบุ หรือต้องการใช้ค่ากลาง
        if (!found && _pluginMap.TryGetValue("fallback", out int fallbackId) && _templateMap.TryGetValue(fallbackId, out var fallbackTemplates))
        {
            ProcessTemplates(fallbackTemplates, placeholders, isFollowUp, ref greeting, ref closing);
        }

        return (greeting, closing);
    }

    private void ProcessTemplates(List<ResponseTemplateRow> templates, Dictionary<string, string> placeholders, bool isFollowUp, ref string greeting, ref string closing)
    {
        if (!isFollowUp)
        {
            var greetings = templates.Where(t => t.Section == "GREETING").ToList();
            if (greetings.Count > 0)
            {
                var text = greetings[Random.Shared.Next(greetings.Count)].TemplateText;
                greeting = AdvancedFormat(text, placeholders);
            }
        }
        else
        {
            string[] shortGreetings = new[] { "สำหรับเรื่องนี้ ", "ในส่วนนี้ ", "นอกจากนี้ ", "" };
            greeting = shortGreetings[Random.Shared.Next(shortGreetings.Length)];
        }

        var closings = templates.Where(t => t.Section == "CLOSING").ToList();
        if (closings.Count > 0)
        {
            var text = closings[Random.Shared.Next(closings.Count)].TemplateText;
            closing = AdvancedFormat(text, placeholders);
        }
    }

    private string AdvancedFormat(string template, Dictionary<string, string> placeholders)
    {
        if (string.IsNullOrEmpty(template)) return template;
        
        var sb = new StringBuilder(template);
        foreach (var kvp in placeholders)
        {
            sb.Replace($"{{{kvp.Key}}}", kvp.Value);
        }
        return sb.ToString();
    }

    private static bool IsFemaleGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender)) return false;
        var value = gender.Trim();
        return value.Equals("F", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Female", StringComparison.OrdinalIgnoreCase);
    }
}