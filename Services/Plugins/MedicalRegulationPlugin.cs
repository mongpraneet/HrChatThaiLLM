using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.SemanticKernel;

namespace HrChatThaiLLM.Server.Services.Plugins;

public class MedicalRegulationPlugin
{
    private readonly IWebHostEnvironment _env;
    private static readonly List<string[]> _cachedExcelRows = new();
    private static readonly List<MedicalExpenseRule> _rules = new();
    private static readonly object _lock = new();
    private static bool _isLoaded = false;

    public MedicalRegulationPlugin(IWebHostEnvironment env)
    {
        _env = env;
        PreloadRegulation();
    }

    private void PreloadRegulation()
    {
        if (_isLoaded) return;

        lock (_lock)
        {
            if (_isLoaded) return;

            try
            {
                var filePath = Path.Combine(_env.WebRootPath, "file", "Medical expense.xlsx");
                if (!File.Exists(filePath)) return;

                using var workbook = new XLWorkbook(filePath);
                var worksheet = workbook.Worksheet(1);
                var rows = worksheet.RowsUsed();

                _cachedExcelRows.Clear();
                _rules.Clear();

                foreach (var row in rows)
                {
                    var cellValues = row.Cells().Select(c => c.GetString().Trim()).ToArray();
                    if (cellValues.Length > 0 && cellValues.Any(v => !string.IsNullOrEmpty(v)))
                    {
                        _cachedExcelRows.Add(cellValues);
                    }
                }

                BuildRulesFromRows();
                _isLoaded = true;
            }
            catch
            {
                // ignore and fallback in public method
            }
        }
    }

    [KernelFunction("get_medical_regulation")]
    [Description("อ่านข้อมูลตารางระเบียบค่ารักษาพยาบาลและวงเงินการเบิกจ่ายตามระดับพนักงานและอายุงาน")]
    public async Task<string> GetMedicalRegulation(
        [Description("รหัสพนักงาน")] string employeeId = "",
        [Description("คำถามจากผู้ใช้ เช่น อายุงาน 5 ปี ระดับ 5 เบิกได้เท่าไหร่")] string question = "")
    {
        if (!_isLoaded)
        {
            PreloadRegulation();
        }

        if (_cachedExcelRows.Count == 0)
        {
            string[] emptyBodyFormats =
            [
                "⚠️ ไม่พบข้อมูลระเบียบค่ารักษาพยาบาลในระบบขณะนี้",
                "🔍 ไม่พบตารางวงเงินสิทธิเบิกค่ารักษาพยาบาล",
                "📋 ยังไม่มีข้อมูลไฟล์ระเบียบค่ารักษาพยาบาล",
                "🛑 ระบบไม่พบไฟล์ Medical expense.xlsx"
            ];
            return emptyBodyFormats[Random.Shared.Next(emptyBodyFormats.Length)];
        }

        if (TryExtractLevelAndYears(question, out var level, out var years))
        {
            if (level >= 12)
            {
                return "ระดับ 12 ขึ้นไปให้อยู่ในดุลยพินิจของผู้บริหาร";
            }

            var amount = GetAllowance(level, years);
            if (amount > 0)
            {
                return $"ระดับตำแหน่ง {level} อายุงาน {years:0.##} ปี สามารถเบิกค่ารักษาพยาบาลได้ {amount:N0} บาท";
            }

            return "ไม่พบเกณฑ์วงเงินที่ตรงกับระดับและอายุงานที่ระบุ";
        }

        // fallback: show table summary when no calculation intent found
        var headers = _cachedExcelRows.First();
        var lines = new List<string>
        {
            "📘 ตารางระเบียบค่ารักษาพยาบาล",
            "| " + string.Join(" | ", headers) + " |",
            "| " + string.Join(" | ", headers.Select(_ => "---")) + " |"
        };

        foreach (var row in _cachedExcelRows.Skip(1))
        {
            lines.Add("| " + string.Join(" | ", row) + " |");
        }

        return await Task.FromResult(string.Join("\n", lines));
    }

    private static bool TryExtractLevelAndYears(string question, out int level, out double years)
    {
        level = 0;
        years = 0;

        var q = (question ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(q)) return false;

        var levelMatch = Regex.Match(q, @"(?:ระดับ|level)\s*[:=]?\s*(\d+)", RegexOptions.IgnoreCase);
        if (!levelMatch.Success)
        {
            levelMatch = Regex.Match(q, @"\b(\d+)\s*(?:ปี)?\s*ระดับ\s*(\d+)", RegexOptions.IgnoreCase);
            if (levelMatch.Success)
            {
                // second group is level for this form
                _ = int.TryParse(levelMatch.Groups[2].Value, out level);
            }
        }
        else
        {
            _ = int.TryParse(levelMatch.Groups[1].Value, out level);
        }

        var yearMatch = Regex.Match(q, @"(?:อายุงาน|years?|ทำงานมา)\s*[:=]?\s*(\d+(?:[\.,]\d+)?)", RegexOptions.IgnoreCase);
        if (!yearMatch.Success)
        {
            yearMatch = Regex.Match(q, @"(\d+(?:[\.,]\d+)?)\s*ปี", RegexOptions.IgnoreCase);
        }

        if (!yearMatch.Success) return false;
        var y = yearMatch.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(y, NumberStyles.Any, CultureInfo.InvariantCulture, out years)) return false;

        return level > 0;
    }

    private static void BuildRulesFromRows()
    {
        foreach (var row in _cachedExcelRows)
        {
            if (row.Length == 0) continue;
            if (!int.TryParse(CleanNumeric(GetCell(row, 0)), out var level)) continue;
            // ระดับจากไฟล์จริงอยู่คอลัมน์ A (#) ช่วง 7-12
            if (level < 7 || level > 11) continue;

            // รองรับรูปแบบ merge cell ในไฟล์:
            // 1-3 ปี อาจอยู่คอลัมน์ 3 หรือ 4
            // 3-5 ปี อาจอยู่คอลัมน์ 5 หรือ 6
            // 5-8 ปี อยู่คอลัมน์ 7
            // 8 ปีขึ้นไป อยู่คอลัมน์ 8
            AddRule(level, 1, 3, ParseAmount(GetCell(row, 2), GetCell(row, 3)));
            AddRule(level, 3, 5, ParseAmount(GetCell(row, 4), GetCell(row, 5)));
            AddRule(level, 5, 8, ParseAmount(GetCell(row, 6)));
            AddRule(level, 8, double.MaxValue, ParseAmount(GetCell(row, 7)));
        }
    }

    private static void AddRule(int level, double minYears, double maxYears, decimal amount)
    {
        if (amount <= 0) return;
        _rules.Add(new MedicalExpenseRule
        {
            Level = level,
            MinYears = minYears,
            MaxYears = maxYears,
            Amount = amount
        });
    }

    private static decimal GetAllowance(int level, double yearsOfService)
    {
        var rule = _rules.FirstOrDefault(r =>
            r.Level == level &&
            yearsOfService >= r.MinYears &&
            yearsOfService < r.MaxYears);
        return rule?.Amount ?? 0;
    }

    private static decimal ParseAmount(params string[] values)
    {
        foreach (var val in values)
        {
            var clean = CleanNumeric(val);
            if (string.IsNullOrWhiteSpace(clean) || clean == "-") continue;
            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.GetCultureInfo("th-TH"), out d)) return d;
        }
        return 0;
    }

    private static string GetCell(string[] row, int index) => index >= 0 && index < row.Length ? row[index] : "";

    private static string CleanNumeric(string s)
    {
        return (s ?? string.Empty).Trim()
            .Replace("฿", "")
            .Replace("บาท", "")
            .Replace(",", "")
            .Replace(" ", "");
    }

    private sealed class MedicalExpenseRule
    {
        public int Level { get; set; }
        public double MinYears { get; set; }
        public double MaxYears { get; set; }
        public decimal Amount { get; set; }
    }
}
