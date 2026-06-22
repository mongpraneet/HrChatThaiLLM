using System.ComponentModel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.SemanticKernel;

namespace HrChatThaiLLM.Server.Services.Plugins;

public class CsvIntentPlugin
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    // เก็บเป็น List อ็อบเจกต์เพื่อนำไปสุ่มสไตล์แปรผันได้
    private static readonly List<CsvRecord> _cachedRecords = new();
    private static readonly List<CsvRecord> _cachedSubIntents = new();
    private static readonly object _lock = new();
    private static bool _isLoaded = false;

    public CsvIntentPlugin(IWebHostEnvironment env, IConfiguration config)
    {
        _env = env;
        _config = config;
        PreloadCsvData();
    }

    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _isLoaded = false;
        }
    }

    /// <summary>
    /// ฟังก์ชันโหลดข้อมูลจากไฟล์ CSV เข้าสู่หน่วยความจำในรูปแบบโครงสร้าง Object (ทำงานรอบเดียวตอนโปรแกรม Start)
    /// </summary>
    private void PreloadCsvData()
    {
        if (_isLoaded) return;

        lock (_lock)
        {
            if (_isLoaded) return;

            try
            {
                var relativePath = _config["FilePaths:CsvIntentCompany"] ?? "file/Intent/intent_about_company.csv";
                var filePath = Path.Combine(_env.WebRootPath, relativePath.Replace('/', '\\'));

                if (!File.Exists(filePath)) return;

                var lines = File.ReadAllLines(filePath);
                _cachedRecords.Clear();

                // ข้ามบรรทัดแรกที่เป็น Header (Text,Intent) แล้วแปลงเข้าสู่โครงสร้างโมเดลลิสต์
                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // CSV ชุดนี้คอลัมน์แรกคือ Text (Key ของ SubIntent) คอลัมน์หลังคือ Intent (คำตอบ)
                    var idx = line.IndexOf(',');
                    if (idx > 0 && idx < line.Length - 1)
                    {
                        var text = line[..idx].Trim().Trim('"', '“', '”');
                        //var intent = line[(idx + 1)..].Trim().Trim('"', '“', '”');
                        var intent = line[(idx + 1)..].Trim().Trim('"', '“', '”').Replace("\\n", "\n");
                        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(intent)) continue;

                        _cachedRecords.Add(new CsvRecord
                        {
                            Text = text,
                            Intent = intent
                        });
                    }
                }

                var relativeSubIntentPath = _config["FilePaths:CsvIntentSubIntent"] ?? "file/csvintent_subintent_training_data.csv";
                var subIntentFilePath = Path.Combine(_env.WebRootPath, relativeSubIntentPath.Replace('/', '\\'));
                if (File.Exists(subIntentFilePath))
                {
                    var subIntentLines = File.ReadAllLines(subIntentFilePath);
                    _cachedSubIntents.Clear();
                    foreach (var line in subIntentLines.Skip(1))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var idx = line.IndexOf(',');
                        if (idx > 0 && idx < line.Length - 1)
                        {
                            var text = line[..idx].Trim().Trim('"', '“', '”');
                            var intent = line[(idx + 1)..].Trim().Trim('"', '“', '”');
                            if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(intent))
                            {
                                _cachedSubIntents.Add(new CsvRecord { Text = text, Intent = intent });
                            }
                        }
                    }
                }

                _isLoaded = true;
            }
            catch
            {
                // ปล่อยให้ระบบข้ามกรณีผิดพลาดเพื่อไปจัดการสุ่มข้อความแจ้งเตือนเปล่าที่ GetIntentTrainingData แทน
            }
        }
    }

    [KernelFunction("get_intent_training_data")]
    [Description("อ่านข้อมูลทั้งหมดจากไฟล์ intent_training_data.csv เพื่อใช้ในการวิเคราะห์หมวดหมู่ข้อความ")]
    public async Task<string> GetIntentTrainingData(
        [Description("รหัสพนักงาน (ระบบส่งมาตามโครงสร้างหลักแต่ไม่ได้ใช้งานจริง)")] string employeeId = "",
        [Description("คำถามของผู้ใช้เพื่อค้นหาคำตอบจากคอลัมน์ Text")] string question = "",
        [Description("SubIntent ที่ทำนายได้จาก AI")] string predictedSubIntent = "")
    {
        if (!_isLoaded)
        {
            PreloadCsvData();
        }

        // 🎲 สุ่มเลือกบริบทกรณี "ไม่พบไฟล์หรือคลังข้อมูลว่างเปล่า"
        if (_cachedRecords.Count == 0)
        {
            string[] emptyBodyFormats = [
                "⚠️ **คลังข้อมูลเอกสารทั่วไป**: ระบบไม่พบข้อมูลถาม-ตอบในไฟล์คลังความรู้ขณะนี้ค่ะ",
                "🔍 **ผลการสแกน Knowledge Base**: ปัจจุบันยังไม่มีรายการข้อมูลจัดหมวดหมู่ในฐานระบบหลักค่ะ",
                "📋 **คลังข้อมูล Intent Training Data**\n❌ ยังไม่มีรายการประโยคตัวอย่างบันทึกไว้ในระบบคลาวด์ค่ะ",
                "🛑 *ระบบจัดการคลังความรู้:* ไม่พบรายการข้อมูล CSV สำเร็จรูปสำหรับใช้วิเคราะห์คำตอบในระบบองค์กร"
            ];
            return emptyBodyFormats[Random.Shared.Next(emptyBodyFormats.Length)];
        }

        // โหมดค้นหาจาก SubIntent (เมื่อ AI ทำนายได้ "CompanyProfile")
        if (!string.IsNullOrWhiteSpace(predictedSubIntent) && !string.Equals(predictedSubIntent, "OutOfScope", StringComparison.OrdinalIgnoreCase))
        {
            var match = _cachedRecords.FirstOrDefault(r =>
                string.Equals(r.Text.Trim(), predictedSubIntent.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return await Task.FromResult(match.Intent.Trim());
            }
        }

        // โหมดค้นหาเฉพาะคำถาม (กรณี AI หลุดเป็น OutOfScope แต่ยังมีคำถาม)
        var q = Norm(question);
        if (!string.IsNullOrWhiteSpace(q))
        {
            // 1. ค้นหาในคลัง subintent ก่อนว่าประโยคคำถามนี้ ตรงกับ SubIntent Key ไหน
            var subIntentMatch = _cachedSubIntents.FirstOrDefault(s =>
                string.Equals(Norm(s.Text), q, StringComparison.OrdinalIgnoreCase) ||
                q.Contains(Norm(s.Text), StringComparison.OrdinalIgnoreCase));

            if (subIntentMatch != null)
            {
                // 2. นำ SubIntent Key ที่หาได้ไปดึงคำตอบจากคลังคำตอบหลัก
                var match = _cachedRecords.FirstOrDefault(r =>
                    string.Equals(r.Text.Trim(), subIntentMatch.Intent.Trim(), StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    return await Task.FromResult(match.Intent.Trim());
                }
            }

            return await Task.FromResult("ไม่พบข้อมูลตามหัวข้อที่ถาม");
        }

        // ════════════════════════════════════════════════════════════════════════
        // 🎲 เริ่มกระบวนการสุ่มฟอร์แมตโครงสร้างเนื้อหา 4 บริบท (กรณีเรียกดูภาพรวมทั้งหมด)
        // ════════════════════════════════════════════════════════════════════════
        int layoutStyleIndex = Random.Shared.Next(4);
        var formattedLines = new List<string>();

        // วนลูปสร้าง Line Items แปลงรูปแบบตามดัชนีสไตล์ปฏิบัติการที่สุ่มได้
        foreach (var item in _cachedRecords)
        {
            string line = layoutStyleIndex switch
            {
                0 => $"• หมวดหมู่: **{item.Text}** │ รายละเอียด: \"{item.Intent}\"",
                1 => $"🔹 `[{item.Text}]` ➔ รายละเอียดข้อมูล: `{item.Intent}`",
                2 => $"┌ 📌 **หมวดหมู่เจตนา**: {item.Text}\n└ 💬 **เนื้อหา**: \"{item.Intent}\"",
                _ => $"➔ {item.Text} » \"{item.Intent}\" 📂"
            };
            formattedLines.Add(line);
        }

        string contentSection = string.Join("\n", formattedLines);

        // 4 โครงสร้างบริบทครอบภาพรวมใหญ่ของตัวเนื้อหาหลัก
        string[] filledBodyFormats = [
            $"""
            📋 **สรุปรายการชุดข้อมูลวิเคราะห์หมวดหมู่ข้อความพนักงาน (Intent Data)**
            {contentSection}
            """,
            $"""
            📊 **คลังข้อมูลคีย์เวิร์ดประโยคและนโยบายเอกสารทั่วไป (Knowledge Base)**
            {contentSection}
            """,
            $"""
            📚 **ฐานข้อมูลสำหรับจัดทำระบบจำแนกทิศทางคำตอบประจำองค์กร**
            {contentSection}
            """,
            $"""
            📁 **รายละเอียดชุดโครงสร้างฝึกจำแนกเจตนาคำถามพนักงาน (CSV Training Document)**
            {contentSection}
            """
        ];

        return await Task.FromResult(filledBodyFormats[layoutStyleIndex]);
    }

    // โครงสร้างคลาสย่อยเก็บโมเดลชุดฝึกฝน
    private class CsvRecord
    {
        public string Text { get; set; } = "";
        public string Intent { get; set; } = "";
    }

    private static string Norm(string s)
    {
        return (s ?? string.Empty)
            .Trim()
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace("  ", " ");
    }
}