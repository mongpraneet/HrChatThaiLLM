using System.Text;
using System.Text.RegularExpressions;

namespace HrChatThaiLLM.Server.Services;

/// <summary>
/// แปลงวันที่ทุกรูปแบบในข้อความให้เป็น yyyy-MM-dd (ค.ศ.) ก่อนส่งต่อให้ Plugin / LLM
///
/// รูปแบบที่รองรับ:
///   dd/MM/yy     →  14/05/69   (พ.ศ. 2 หลัก)
///   dd/MM/yyyy   →  14/05/2569 (พ.ศ. 4 หลัก) หรือ 14/05/2026 (ค.ศ.)
///   dd.MM.yy     →  14.05.69
///   dd.MM.yyyy   →  14.05.2569 / 14.05.2026
///   dd-MM-yy     →  14-05-69
///   dd-MM-yyyy   →  14-05-2569 / 14-05-2026
///   ตัวหนังสือติดกับวันที่:  วันที่18.05.69, วันที่18.05.2026
///   date range:  วันที่18.05.69 ถึง วันที่20.05.69
/// </summary>
public static class DateNormalizer
{
    // ── จับตัวเลขวันที่ทุกรูปแบบ (separator อาจเป็น / . -)
    //    และอนุญาตให้ตัวหนังสือนำหน้าติดกันได้ เช่น "วันที่14.05.69"
    private static readonly Regex _datePattern = new(
        @"(?<=[^\d]|^)(\d{1,2})[./-](\d{1,2})[./-](\d{2,4})(?=[^\d]|$)",
        RegexOptions.Compiled);

    // ── คำนำหน้าวันที่ในภาษาไทยที่อาจติดกับตัวเลข
    private static readonly Regex _thaiDatePrefix = new(
        @"(วันที่|ว\.ที่|date:?|ที่)\s*(?=\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// แปลงวันที่ทั้งหมดในข้อความ แล้วคืน string ที่ normalize แล้ว
    /// เช่น "เข้างานวันที่14.05.69 ถึงวันที่20.05.69"
    ///   →  "เข้างานวันที่ 2026-05-14 ถึงวันที่ 2026-05-20"
    /// </summary>
    public static string NormalizeThaiDates(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        // Step 1: แยกคำนำหน้าออกจากตัวเลข โดยเพิ่มช่องว่าง
        //         "วันที่14.05.69" → "วันที่ 14.05.69"
        var spaced = _thaiDatePrefix.Replace(input, m => m.Value.TrimEnd() + " ");

        // Step 2: แปลงวันที่แต่ละตัวใน string
        var result = _datePattern.Replace(spaced, m =>
        {
            if (TryConvertToGregorian(
                    m.Groups[1].Value,   // day
                    m.Groups[2].Value,   // month
                    m.Groups[3].Value,   // year (2 หรือ 4 หลัก)
                    out var converted))
                return converted;

            return m.Value; // แปลงไม่ได้ → คงค่าเดิม
        });

        return result;
    }

    /// <summary>
    /// แปลงตัวเลข day/month/year (string) เป็น "yyyy-MM-dd" (ค.ศ.)
    /// คืน true เมื่อแปลงสำเร็จ
    /// </summary>
    public static bool TryConvertToGregorian(
        string dayStr, string monthStr, string yearStr,
        out string converted)
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
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// แปลงปีทุกรูปแบบให้เป็น ค.ศ. 4 หลัก
    ///   2 หลัก (00-99)  → ถือเป็น พ.ศ. 2500+xx → แปลงเป็น ค.ศ. (- 543)
    ///   4 หลัก >= 2400  → ถือเป็น พ.ศ.          → แปลงเป็น ค.ศ. (- 543)
    ///   4 หลัก < 2400   → ถือเป็น ค.ศ. แล้ว     → คงเดิม
    /// </summary>
    public static int NormalizeYear(int raw)
    {
        if (raw < 100)
        {
            // เช่น 69 → พ.ศ. 2569 → ค.ศ. 2026
            raw += 2500;
        }

        if (raw >= 2400)
        {
            // เป็น พ.ศ. → แปลงเป็น ค.ศ.
            raw -= 543;
        }

        return raw;
    }

    /// <summary>
    /// พยายามดึง DateTime เดี่ยวจาก string ที่อาจมีตัวหนังสือปน
    /// ใช้ใน AttendancePlugin แทน TryExtractThaiDate เดิม
    /// </summary>
    public static bool TryExtractDate(string text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = NormalizeThaiDates(text);
        // หลัง normalize จะมีรูปแบบ yyyy-MM-dd
        var m = Regex.Match(normalized, @"\b(\d{4})-(\d{2})-(\d{2})\b");
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[1].Value, out int y)) return false;
        if (!int.TryParse(m.Groups[2].Value, out int mo)) return false;
        if (!int.TryParse(m.Groups[3].Value, out int d)) return false;

        try
        {
            date = new DateTime(y, mo, d);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// พยายามดึง DateTime range (start, end) จาก string
    /// ใช้ใน AttendancePlugin แทน TryExtractThaiDateRange เดิม
    /// รองรับคำว่า "ถึง", "to", "-" คั่นระหว่างวันที่
    /// </summary>
    public static bool TryExtractDateRange(string text, out DateTime startDate, out DateTime endDate)
    {
        startDate = default;
        endDate = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = NormalizeThaiDates(text);

        // จับ yyyy-MM-dd ทั้งหมดที่อยู่ใน string
        var matches = Regex.Matches(normalized, @"\b(\d{4}-\d{2}-\d{2})\b");
        if (matches.Count < 2) return false;

        if (!DateTime.TryParse(matches[0].Value, out startDate)) return false;
        if (!DateTime.TryParse(matches[1].Value, out endDate)) return false;

        // ถ้าลำดับสลับกัน ให้สลับกลับ
        if (endDate < startDate)
            (startDate, endDate) = (endDate, startDate);

        return true;
    }
}