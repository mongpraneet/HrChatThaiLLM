using System.ComponentModel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.SemanticKernel;

namespace HrChatThaiLLM.Server.Services.Plugins;

public class CsvIntentPlugin
{
    private readonly IWebHostEnvironment _env;
    // เปลี่ยนจากเก็บโครงสร้าง String สำเร็จรูป มาเก็บเป็น List อ็อบเจกต์เพื่อนำไปสุ่มสไตล์แปรผันได้
    private static readonly List<CsvRecord> _cachedRecords = new();
    private static readonly object _lock = new();
    private static bool _isLoaded = false;

    public CsvIntentPlugin(IWebHostEnvironment env)
    {
        _env = env;
        PreloadCsvData();
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
                var filePath = Path.Combine(_env.WebRootPath, "file", "intent_training_data.csv");

                if (!File.Exists(filePath)) return;

                var lines = File.ReadAllLines(filePath);
                _cachedRecords.Clear();

                // ข้ามบรรทัดแรกที่เป็น Header (Text,Intent) แล้วแปลงเข้าสู่โครงสร้างโมเดลลิสต์
                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // CSV ชุดนี้คอลัมน์แรกคือ Text (ไม่มี comma) คอลัมน์หลังคือ Intent (อาจมี comma)
                    // จึง split ที่ comma ตัวแรกเท่านั้น
                    var idx = line.IndexOf(',');
                    if (idx > 0 && idx < line.Length - 1)
                    {
                        var text = line[..idx].Trim().Trim('"', '“', '”');
                        var intent = line[(idx + 1)..].Trim().Trim('"', '“', '”');
                        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(intent)) continue;

                        _cachedRecords.Add(new CsvRecord
                        {
                            Text = text,
                            Intent = intent
                        });
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
        [Description("คำถามของผู้ใช้เพื่อค้นหาคำตอบจากคอลัมน์ Text")] string question = "")
    {
        if (!_isLoaded)
        {
            PreloadCsvData();
        }

        // 🎲 สุ่มเลือกบริบทกรณี "ไม่พบไฟล์หรือคลังข้อมูลว่างเปล่า" (4 บริบท)
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

        // โหมดค้นหาเฉพาะคำถาม: ต้องตอบเฉพาะหัวข้อที่ถาม
        var q = Norm(question);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var exact = _cachedRecords.FirstOrDefault(r =>
                string.Equals(Norm(r.Text), q, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return await Task.FromResult(exact.Intent.Trim());

            var contains = _cachedRecords.FirstOrDefault(r =>
                q.Contains(Norm(r.Text), StringComparison.OrdinalIgnoreCase));
            if (contains is not null) return await Task.FromResult(contains.Intent.Trim());

            return await Task.FromResult("ไม่พบข้อมูลตามหัวข้อที่ถาม");
        }

        // ════════════════════════════════════════════════════════════════════════
        // 🎲 เริ่มกระบวนการสุ่มฟอร์แมตโครงสร้างเนื้อหา 4 บริบท (4 Layout Contexts)
        // ════════════════════════════════════════════════════════════════════════
        int layoutStyleIndex = Random.Shared.Next(4);
        var formattedLines = new List<string>();

        // วนลูปสร้าง Line Items แปลงรูปแบบตามดัชนีสไตล์ปฏิบัติการที่สุ่มได้
        foreach (var item in _cachedRecords)
        {
            string line = layoutStyleIndex switch
            {
                0 => $"• หมวดหมู่: **{item.Intent}** │ ประโยคตัวอย่าง: \"{item.Text}\"",
                1 => $"🔹 `[{item.Intent}]` ➔ คีย์เวิร์ดคำถาม: `{item.Text}`",
                2 => $"┌ 📌 **หมวดหมู่เจตนา**: {item.Intent}\n└ 💬 **ตัวอย่างประโยค**: \"{item.Text}\"",
                _ => $"➔ {item.Intent} » \"{item.Text}\" 📂"
            };
            formattedLines.Add(line);
        }

        string contentSection = string.Join("\n", formattedLines);

        // 4 โครงสร้างบริบทครอบภาพรวมใหญ่ของตัวเนื้อหาหลัก (Pure Body Payload)
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
