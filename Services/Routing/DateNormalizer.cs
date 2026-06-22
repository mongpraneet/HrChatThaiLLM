using System.Text;
using System.Text.RegularExpressions;

namespace HrChatThaiLLM.Server.Services;

public static class DateNormalizer
{
    // ── 1. Pattern สำหรับ "ชื่อเดือน" (เช่น เดือน พฤษภาคม 69, พ.ค. 2569)
    private static readonly Regex _monthNameYearPattern = new(
        @"(เดือน\s*)?(มกราคม|กุมภาพันธ์|มีนาคม|เมษายน|พฤษภาคม|มิถุนายน|กรกฎาคม|สิงหาคม|กันยายน|ตุลาคม|พฤศจิกายน|ธันวาคม|ม\.ค\.|ก\.พ\.|มี\.ค\.|เม\.ย\.|พ\.ค\.|มิ\.ย\.|ก\.ค\.|ส\.ค\.|ก\.ย\.|ต\.ค\.|พ\.ย\.|ธ\.ค\.|มกรา|กุมภา|มีนา|เมษา|พฤษภา|มิถุนา|กรกฎา|สิงหา|กันยา|ตุลา|พฤศจิกา|ธันวา)\s*(ปี\s*)?(\d{2,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── 2. Pattern ใหม่ สำหรับ "ตัวเลขเดือน" (เช่น เดือน 5 68, เดือน 05 ปี 2026) ──
    // บังคับต้องมีคำว่า "เดือน" นำหน้าเสมอ ป้องกันการสับสนกับรูปแบบวันที่
    private static readonly Regex _numericMonthYearPattern = new(
        @"(เดือน\s+)(1[0-2]|0?[1-9])\s*(ปี\s*)?(\d{2,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> _thaiMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        {"มกราคม", 1}, {"ม.ค.", 1}, {"มกรา", 1},
        {"กุมภาพันธ์", 2}, {"ก.พ.", 2}, {"กุมภา", 2},
        {"มีนาคม", 3}, {"มี.ค.", 3}, {"มีนา", 3},
        {"เมษายน", 4}, {"เม.ย.", 4}, {"เมษา", 4},
        {"พฤษภาคม", 5}, {"พ.ค.", 5}, {"พฤษภา", 5},
        {"มิถุนายน", 6}, {"มิ.ย.", 6}, {"มิถุนา", 6},
        {"กรกฎาคม", 7}, {"ก.ค.", 7}, {"กรกฎา", 7},
        {"สิงหาคม", 8}, {"ส.ค.", 8}, {"สิงหา", 8},
        {"กันยายน", 9}, {"ก.ย.", 9}, {"กันยา", 9},
        {"ตุลาคม", 10}, {"ต.ค.", 10}, {"ตุลา", 10},
        {"พฤศจิกายน", 11}, {"พ.ย.", 11}, {"พฤศจิกา", 11},
        {"ธันวาคม", 12}, {"ธ.ค.", 12}, {"ธันวา", 12}
    };

    private static readonly Regex _datePattern = new(
        @"(?<=[^\d]|^)(\d{1,2})[./-](\d{1,2})[./-](\d{2,4})(?=[^\d]|$)",
        RegexOptions.Compiled);

    private static readonly Regex _thaiDatePrefix = new(
        @"(วันที่|ว\.ที่|date:?|ที่)\s*(?=\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string NormalizeThaiDates(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        // Step 0: ดักจับคำว่า "เดือนนี้" หรือ "เดือนที่แล้ว"
        if (input.Contains("เดือนนี้"))
        {
            var d = DateTime.Today;
            input = input.Replace("เดือนนี้", $" {d.Year:0000}-{d.Month:00}-01 ถึง {d.Year:0000}-{d.Month:00}-{DateTime.DaysInMonth(d.Year, d.Month):00} ");
        }
        if (input.Contains("เดือนที่แล้ว"))
        {
            var d = DateTime.Today.AddMonths(-1);
            input = input.Replace("เดือนที่แล้ว", $" {d.Year:0000}-{d.Month:00}-01 ถึง {d.Year:0000}-{d.Month:00}-{DateTime.DaysInMonth(d.Year, d.Month):00} ");
        }

        // Step 1.1: แปลงชื่อเดือน+ปี ให้เป็นช่วงวันที่ 
        input = _monthNameYearPattern.Replace(input, m =>
        {
            string monthStr = m.Groups[2].Value;
            string yearStr = m.Groups[4].Value;

            if (_thaiMonths.TryGetValue(monthStr, out int month))
            {
                if (int.TryParse(yearStr, out int year))
                {
                    int normalYear = NormalizeYear(year);
                    if (normalYear > 0)
                    {
                        int daysInMonth = DateTime.DaysInMonth(normalYear, month);
                        return $" {normalYear:0000}-{month:00}-01 ถึง {normalYear:0000}-{month:00}-{daysInMonth:00} ";
                    }
                }
            }
            return m.Value;
        });

        // Step 1.2: แปลง "ตัวเลขเดือน+ปี" ให้เป็นช่วงวันที่ (อัปเดตใหม่)
        input = _numericMonthYearPattern.Replace(input, m =>
        {
            string monthStr = m.Groups[2].Value; // จับตัวเลข 1-12
            string yearStr = m.Groups[4].Value;  // จับตัวเลขปี

            if (int.TryParse(monthStr, out int month) && month >= 1 && month <= 12)
            {
                if (int.TryParse(yearStr, out int year))
                {
                    int normalYear = NormalizeYear(year);
                    if (normalYear > 0)
                    {
                        int daysInMonth = DateTime.DaysInMonth(normalYear, month);
                        return $" {normalYear:0000}-{month:00}-01 ถึง {normalYear:0000}-{month:00}-{daysInMonth:00} ";
                    }
                }
            }
            return m.Value;
        });

        // Step 2: แยกคำนำหน้าออกจากตัวเลข
        var spaced = _thaiDatePrefix.Replace(input, m => m.Value.TrimEnd() + " ");

        // Step 3: แปลงวันที่แต่ละตัวใน string ให้เป็นฟอร์แมต yyyy-MM-dd
        var result = _datePattern.Replace(spaced, m =>
        {
            if (TryConvertToGregorian(
                    m.Groups[1].Value,
                    m.Groups[2].Value,
                    m.Groups[3].Value,
                    out var converted))
                return converted;

            return m.Value;
        });

        return result;
    }

    public static bool TryConvertToGregorian(string dayStr, string monthStr, string yearStr, out string converted)
    {
        converted = string.Empty;
        if (!int.TryParse(dayStr, out int day)) return false;
        if (!int.TryParse(monthStr, out int month)) return false;
        if (!int.TryParse(yearStr, out int year)) return false;

        year = NormalizeYear(year);
        if (year <= 0) return false;

        try
        {
            var date = new DateTime(year, month, day);
            converted = date.ToString("yyyy-MM-dd");
            return true;
        }
        catch { return false; }
    }

    public static int NormalizeYear(int raw)
    {
        if (raw < 100) raw += 2500;
        if (raw >= 2400) raw -= 543;
        return raw;
    }

    public static bool TryExtractDate(string text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = NormalizeThaiDates(text);
        var m = Regex.Match(normalized, @"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)");
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[1].Value, out int y)) return false;
        if (!int.TryParse(m.Groups[2].Value, out int mo)) return false;
        if (!int.TryParse(m.Groups[3].Value, out int d)) return false;

        try { date = new DateTime(y, mo, d); return true; }
        catch { return false; }
    }

    public static bool TryExtractDateRange(string text, out DateTime startDate, out DateTime endDate)
    {
        startDate = default;
        endDate = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = NormalizeThaiDates(text);
        var matches = Regex.Matches(normalized, @"(?<!\d)(\d{4}-\d{2}-\d{2})(?!\d)");
        if (matches.Count < 2) return false;

        if (!DateTime.TryParse(matches[0].Value, out startDate)) return false;
        if (!DateTime.TryParse(matches[1].Value, out endDate)) return false;

        if (endDate < startDate) (startDate, endDate) = (endDate, startDate);
        return true;
    }
}