using Dapper;
using Microsoft.Data.SqlClient;
using HrChatThaiLLM.Server.Models;
using System.Collections.Concurrent;

namespace HrChatThaiLLM.Server.Services;

public interface IResponseComposer
{
    Task RefreshCacheAsync();
    string? CheckProfanity(string message, string gender, string employeeName);
    string ComposeResponse(string pluginKey, string bodyContent, string employeeName, string gender);
}

public class ResponseComposer : IResponseComposer
{
    private readonly string _connectionString;
    private readonly ILogger<ResponseComposer> _logger;

    // In-Memory Caches สำหรับการประมวลผลความเร็วสูง
    private static readonly ConcurrentBag<string> _profanityList = new();
    private static readonly ConcurrentDictionary<string, int> _pluginMap = new(); // PluginKey -> PluginId
    private static readonly ConcurrentDictionary<int, List<ResponseTemplateRow>> _templateMap = new(); // PluginId -> Templates
    private static readonly List<string> _profanityWarnings = new();

    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static bool _isInitialized = false;

    public ResponseComposer(IConfiguration config, ILogger<ResponseComposer> logger)
    {
        _connectionString = config.GetConnectionString("ChatHistoryDatabase")
            ?? throw new InvalidOperationException("ChatHistoryDatabase string not found");
        _logger = logger;
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

            // 1. โหลดคำหยาบ
            var badWords = await conn.QueryAsync<string>("SELECT BadWord FROM ProfanityBlacklist WHERE IsActive = 1");
            _profanityList.Clear();
            foreach (var word in badWords) _profanityList.Add(word.Trim());

            // 2. โหลดแมปปิ้งปลั๊กอิน
            var plugins = await conn.QueryAsync<PluginRow>("SELECT Id, PluginKey FROM Plugins");
            _pluginMap.Clear();
            foreach (var p in plugins) _pluginMap.TryAdd(p.PluginKey, p.Id);

            // 3. โหลดเทมเพลตคำตอบทั้งหมด
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
            _logger.LogInformation("✅ ResponseComposer: แคชข้อมูลเรียบร้อยแล้ว (คำหยาบ: {0} คำ, เทมเพลต: {1} รายการ)", _profanityList.Count, templates.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ โหลดข้อมูลล้มเหลวใน ResponseComposer");
        }
    }

    // ตรวจสอบคำหยาบ หากพบจะสุ่มคำเตือนส่งคืนทันที
    public string? CheckProfanity(string message, string gender, string employeeName)
    {
        EnsureInitializedAsync().GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(message)) return null;

        // ตรวจสอบคำหยาบจากคลังความรู้
        bool hasBadWord = _profanityList.Any(badWord => message.Contains(badWord, StringComparison.OrdinalIgnoreCase));

        if (hasBadWord && _profanityWarnings.Count > 0)
        {
            // แปลงรหัสเพศเป็นคำลงท้ายหางเสียงพนักงาน
            string politeWord = IsFemaleGender(gender) ? "ค่ะ" : "ครับ";
            string fullNameWithKhun = $"คุณ {employeeName}";

            // สุ่มเลือกคำเตือนคำหยาบจากตาราง ResponseTemplates
            int randomIndex = Random.Shared.Next(_profanityWarnings.Count);
            string templateText = _profanityWarnings[randomIndex];

            try
            {
                // 🚀 ทำการแทนค่า {0} = ค่ะ/ครับ, {1} = คุณ... ลงในข้อความเตือนทันที
                return string.Format(templateText, politeWord, fullNameWithKhun);
            }
            catch (FormatException)
            {
                // ป้องกันกรณีเทมเพลตใน DB เขียนรูปแบบวงเล็บปีกกา { } ผิดพลาด
                return templateText.Replace("{0}", politeWord).Replace("{1}", fullNameWithKhun);
            }
        }

        return null;
    }

    // ประกอบร่างข้อความ หัว - เนื้อหา - ท้าย
    public string ComposeResponse(string pluginKey, string bodyContent, string employeeName, string gender)
    {
        EnsureInitializedAsync().GetAwaiter().GetResult();

        string politeWord = IsFemaleGender(gender) ? "ค่ะ" : "ครับ";
        string fullNameWithKhun = $"คุณ {employeeName}";

        // ดึงข้อความทดแทนตระกูลสุ่มอนุภาค (เช่น สองช่อง {0} เป็นชื่อพนักงาน)
        string greeting = "สวัสดีครับ";
        string closing = "ยินดีที่ได้ช่วยเหลือครับ";

        // ค้นหา PluginId จากตารางคีย์เวิร์ด
        if (_pluginMap.TryGetValue(pluginKey, out int pluginId) && _templateMap.TryGetValue(pluginId, out var templates))
        {
            // 1. สุ่มฝั่ง GREETING
            var greetings = templates.Where(t => t.Section == "GREETING").ToList();
            if (greetings.Count > 0)
            {
                var text = greetings[Random.Shared.Next(greetings.Count)].TemplateText;
                greeting = string.Format(text, politeWord, fullNameWithKhun);
            }

            // 2. สุ่มฝั่ง CLOSING
            var closings = templates.Where(t => t.Section == "CLOSING").ToList();
            if (closings.Count > 0)
            {
                var text = closings[Random.Shared.Next(closings.Count)].TemplateText;
                closing = string.Format(text, politeWord, fullNameWithKhun);
            }
        }
        else
        {
            // พยายามดึงข้อมูลจากตาราง Fallback (PluginId 7) หากไม่พบคู่ปลั๊กอินตรงตัว
            if (_pluginMap.TryGetValue("Fallback", out int fallbackId) && _templateMap.TryGetValue(fallbackId, out var fallbackTemplates))
            {
                var fGreetings = fallbackTemplates.Where(t => t.Section == "GREETING").ToList();
                if (fGreetings.Count > 0) greeting = string.Format(fGreetings[Random.Shared.Next(fGreetings.Count)].TemplateText, employeeName);

                var fClosings = fallbackTemplates.Where(t => t.Section == "CLOSING").ToList();
                if (fClosings.Count > 0) closing = string.Format(fClosings[Random.Shared.Next(fClosings.Count)].TemplateText, employeeName);
            }
        }

        // ประกอบข้อความเข้าด้วยกันในรูปแบบ Markdown
        return $"""
            {greeting}

            {bodyContent.Trim()}

            {closing}
            """;
    }

    private static bool IsFemaleGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender)) return false;
        var value = gender.Trim();
        return value.Equals("F", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Female", StringComparison.OrdinalIgnoreCase);
    }
}
