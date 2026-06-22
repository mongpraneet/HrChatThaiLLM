using System.ComponentModel;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.SemanticKernel;

namespace HrChatThaiLLM.Server.Services.Plugins;

public class AttendancePlugin
{
    private readonly string _faceScan1Conn;
    private readonly string _faceScan2Conn;
    private readonly string _faceScan3Conn;
    private readonly ILogger<AttendancePlugin> _logger;

    public AttendancePlugin(IConfiguration config, ILogger<AttendancePlugin> logger)
    {
        _logger = logger;
        _faceScan1Conn = config.GetConnectionString("FaceScan1DB")
            ?? throw new InvalidOperationException("FaceScan1DB connection string not found");
        _faceScan2Conn = config.GetConnectionString("FaceScan2DB")
            ?? throw new InvalidOperationException("FaceScan2DB connection string not found");
        _faceScan3Conn = config.GetConnectionString("FaceScan3DB")
            ?? throw new InvalidOperationException("FaceScan3DB connection string not found");
    }

    [KernelFunction("get_recent_attendance")]
    [Description("ดึงข้อมูลเวลาเข้าออกงานล่าสุดจาก FaceScan 3 Server แบบขนาน (รองรับกะข้ามวันและลืมสแกน)")]
    public async Task<string> GetRecentAttendance(
         [Description("รหัสพนักงาน")] string employeeId,
         [Description("คำถามจากผู้ใช้ เช่น เมื่อวานเข้างานกี่โมง, เวลาเข้างานล่าสุด")] string question = "",
         [Description("SubIntent ที่ทำนายได้จาก AI (จากไฟล์ attendance_subintent)")] string predictedSubIntent = "",
         [Description("เพศของผู้ใช้งาน (Male/Female)")] string gender = "Male")
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return "ไม่พบรหัสพนักงาน";

        var normalizedEmployeeId = employeeId.Trim();
        var ask = DateNormalizer.NormalizeThaiDates(question?.Trim() ?? string.Empty);
        var normIntent = predictedSubIntent?.Trim() ?? string.Empty;
        string polite = string.Equals(gender, "Female", StringComparison.OrdinalIgnoreCase) ? "ค่ะ" : "ครับ";

        // ── 1. ผสานความฉลาดระหว่าง Keyword เดิม และ Sub-intent จาก AI ──
        var isIntentLatest = normIntent.Equals("AttendanceLatest", StringComparison.OrdinalIgnoreCase);
        var isIntentToday = normIntent.Equals("AttendanceToday", StringComparison.OrdinalIgnoreCase);
        var isIntentYesterday = normIntent.Equals("AttendanceYesterday", StringComparison.OrdinalIgnoreCase);
        var isIntentHistory = normIntent.Equals("AttendanceHistory", StringComparison.OrdinalIgnoreCase);
        var isIntentDateRange = normIntent.Equals("AttendanceDateRange", StringComparison.OrdinalIgnoreCase);

        var askLatest = isIntentLatest ||
                        ask.Contains("ล่าสุด", StringComparison.OrdinalIgnoreCase) ||
                        ask.Contains("last", StringComparison.OrdinalIgnoreCase);

        var askYesterday = isIntentYesterday ||
                           ask.Contains("เมื่อวาน", StringComparison.OrdinalIgnoreCase);

        // ── 2. กำหนดช่วงวันที่ต้องการจริงๆ (Logical Date Range) ──
        var hasExplicitDateRange = DateNormalizer.TryExtractDateRange(ask, out var requestedStart, out var requestedEnd);
        DateTime requestedDate = default;
        var hasExplicitDate = !hasExplicitDateRange && DateNormalizer.TryExtractDate(ask, out requestedDate);

        DateTime reqStart, reqEnd;

        // ลำดับความสำคัญในการตัดสินใจช่วงเวลา (Decision Tree)
        if (hasExplicitDateRange)
        {
            reqStart = requestedStart.Date;
            reqEnd = requestedEnd.Date;
        }
        else if (hasExplicitDate)
        {
            reqStart = requestedDate.Date;
            reqEnd = requestedDate.Date;
        }
        else if (isIntentToday)
        {
            reqStart = DateTime.Today;
            reqEnd = DateTime.Today;
        }
        else if (askYesterday)
        {
            reqStart = DateTime.Today.AddDays(-1);
            reqEnd = reqStart;
        }
        else if (isIntentHistory || isIntentDateRange)
        {
            reqStart = DateTime.Today.AddDays(-7);
            reqEnd = DateTime.Today;
        }
        else
        {
            reqStart = DateTime.Today.AddDays(-7);
            reqEnd = DateTime.Today;
        }

        // ── 3. ขยายช่วงเวลา Query Database (+/- 1 วัน) เพื่อรองรับกะข้ามวัน ──
        var dbStart = reqStart.AddDays(-1);
        var dbEnd = reqEnd.AddDays(1);

        var startDateStr = dbStart.ToString("yyyyMMdd");
        var endDateStr = dbEnd.ToString("yyyyMMdd");
        var startDateOnly = dbStart.ToString("yyyy-MM-dd");
        var endDateOnly = dbEnd.ToString("yyyy-MM-dd");

        // ── 4. ดึงข้อมูลจาก 3 Server พร้อมกัน ──
        var scan1Task = QueryScan1Async(normalizedEmployeeId, startDateStr, endDateStr);
        var scan2Task = QueryScan2Async(normalizedEmployeeId, startDateStr, endDateStr);
        var scan3Task = QueryScan3Async(normalizedEmployeeId, startDateOnly, endDateOnly);

        await Task.WhenAll(scan1Task, scan2Task, scan3Task);

        var allScans = scan1Task.Result
            .Concat(scan2Task.Result)
            .Concat(scan3Task.Result)
            .OrderBy(r => r.EventTime)
            .ToList();

        // ── สร้างข้อความส่วนหัวให้สอดคล้องกับ Intent ──
        string dateMsg;
        if (hasExplicitDateRange)
        {
            // ตรวจสอบว่าเป็นการค้นหา "ทั้งเดือน" หรือไม่
            if (reqStart.Day == 1 && reqEnd.Day == DateTime.DaysInMonth(reqStart.Year, reqStart.Month) && reqStart.Month == reqEnd.Month)
            {
                string[] thaiMonths = { "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน", "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม" };
                dateMsg = $"เดือน {thaiMonths[reqStart.Month]} ปี {reqStart.Year + 543} (วันที่ 1 ถึง {reqEnd.Day})";
            }
            else
            {
                dateMsg = $"ช่วงวันที่ {reqStart:dd/MM/yyyy} ถึง {reqEnd:dd/MM/yyyy}";
            }
        }
        else if (hasExplicitDate) dateMsg = $"วันที่ {reqStart:dd/MM/yyyy}";
        else if (askYesterday) dateMsg = $"เมื่อวาน ({reqStart:dd/MM/yyyy})";
        else if (isIntentToday) dateMsg = $"วันนี้ ({reqStart:dd/MM/yyyy})";
        else dateMsg = "ย้อนหลัง 7 วัน";

        // 🎲 สุ่มเลือกบริบทกรณี "ไม่พบข้อมูล"
        if (allScans.Count == 0)
        {
            string[] emptyBodyFormats = [
                $"⚠️ **รายงานเวลาทำงาน**: ไม่พบข้อมูลบันทึกเวลาเข้าออกงานของรหัสพนักงาน `{normalizedEmployeeId}` สำหรับ{dateMsg} ในระบบ{polite}",
                $"🔍 **ผลการค้นหาเครื่องสแกนใบหน้า**: ยังไม่มีประวัติการบันทึกเวลาเข้างาน ({dateMsg}) ของรหัส `{normalizedEmployeeId}` บนระบบคลาวด์{polite}",
                $"""
                ⏱️ **บันทึกเวลาเข้างาน (สิทธิ์พนักงาน: {normalizedEmployeeId})**
                ❌ ไม่พบประวัติการ Log-in หรือข้อมูลสแกนนิ้ว/ใบหน้าในช่วง{dateMsg} {polite}
                """,
                $"🛑 *ระบบลงเวลาทำงานแจ้งเตือน:* ไม่พบสัญญาณข้อมูลเวลา (Time Attendance Log) สำหรับ{dateMsg} ของพนักงานรหัส `{normalizedEmployeeId}`"
            ];
            return emptyBodyFormats[Random.Shared.Next(emptyBodyFormats.Length)];
        }

        // ── 5. อัลกอริทึมจับคู่กะ (Shift Pairing Algorithm) ──
        var shifts = new List<ShiftRecord>();
        ShiftRecord? currentShift = null;

        foreach (var scan in allScans)
        {
            if (currentShift == null)
            {
                currentShift = new ShiftRecord { FirstScan = scan.EventTime, LastScan = scan.EventTime };
            }
            else
            {
                var totalDuration = (scan.EventTime - currentShift.FirstScan).TotalHours;
                if (totalDuration <= 16)
                {
                    currentShift.LastScan = scan.EventTime;
                }
                else
                {
                    shifts.Add(currentShift);
                    currentShift = new ShiftRecord { FirstScan = scan.EventTime, LastScan = scan.EventTime };
                }
            }
        }
        if (currentShift != null) shifts.Add(currentShift);

        // ── 6. กรองเฉพาะกะที่ครอบคลุมช่วงเวลาที่ผู้ใช้ถาม ──
        var filteredShifts = shifts.Where(s =>
            (s.FirstScan.Date >= reqStart && s.FirstScan.Date <= reqEnd) ||
            (s.LastScan.Date >= reqStart && s.LastScan.Date <= reqEnd)
        ).ToList();

        // 🎲 สุ่มเลือกบริบทกรณี "ไม่พบข้อมูลหลังจับคู่กะ"
        if (!filteredShifts.Any() && !askLatest)
        {
            string[] noShiftFormats = [
                $"⚠️ ข้อมูลเครื่องสแกนมีบันทึก แต่ไม่เข้าเงื่อนไขจับคู่กะงาน ({dateMsg}) ของรหัส `{normalizedEmployeeId}` {polite}",
                $"🔍 ตรวจเช็คโครงสร้างเวลา {dateMsg} แล้ว ไม่พบช่วงเวลาการทำงานที่สมบูรณ์ของรหัส {normalizedEmployeeId} {polite}",
                $"⏱️ *ระบบบันทึกงาน:* รายการสแกนใน{dateMsg} ของรหัส {normalizedEmployeeId} ไม่สามารถจับคู่ออกเป็นกะทำงานปกติได้{polite}",
                $"🛑 ไม่พบการจับคู่กะเข้างานข้ามวันหรือกะปกติในช่วง {dateMsg} ของพนักงานรหัส `{normalizedEmployeeId}` {polite}"
            ];
            return noShiftFormats[Random.Shared.Next(noShiftFormats.Length)];
        }

        int layoutStyleIndex = Random.Shared.Next(4);

        // กรณีถาม "ล่าสุด" -> สุ่มแสดงผลเฉพาะรายการล่าสุด
        if (askLatest)
        {
            var latest = shifts.OrderByDescending(s => s.FirstScan).FirstOrDefault();
            if (latest == null) return "❌ ไม่พบข้อมูลการสแกนเข้าออกงานล่าสุดในระบบ{polite}";

            string[] latestBodyFormats = [
                $"⏱️ **ข้อมูลเวลาเข้าออกงานล่าสุด**\n{FormatShift(latest, layoutStyleIndex)}",
                $"🏃‍♂️ **ประวัติการบันทึกเวลาทำงาน (ล่าสุด)**\n» {FormatShift(latest, layoutStyleIndex)}",
                $"""
                📅 **สถานะการลงเวลาทำงานล่าสุดของคุณ**
                ┌──────────────────────────────────────────┐
                │ {FormatShift(latest, layoutStyleIndex)}
                └──────────────────────────────────────────┘
                """,
                $"🔔 *บันทึกสแกนใบหน้าล่าสุดจากระบบเซิร์ฟเวอร์:* {FormatShift(latest, layoutStyleIndex)}"
            ];
            return latestBodyFormats[Random.Shared.Next(latestBodyFormats.Length)];
        }

        // ── 7. วนลูปตรวจสอบแต่ละวัน (เรียงจากวันที่ล่าสุด ไปหาเก่าสุด) ──
        var results = new List<string>();

        // ถ้าค้นหาแบบระบุเดือน/ช่วงเวลา (เช่น เดือนพฤษภาคม) ให้เรียงจาก น้อยไปหามาก (1 -> 31)
        // ถ้าค้นหาแบบทั่วไป (เช่น ย้อนหลัง 7 วัน) ให้เรียงจาก มากไปหาน้อย (ล่าสุดขึ้นก่อน)
        bool sortAscending = hasExplicitDateRange;

        // สร้างลิสต์ของวันที่ตามทิศทางที่ต้องการ
        var loopDates = new List<DateTime>();
        if (sortAscending)
        {
            for (var d = reqStart.Date; d <= reqEnd.Date; d = d.AddDays(1)) loopDates.Add(d);
        }
        else
        {
            for (var d = reqEnd.Date; d >= reqStart.Date; d = d.AddDays(-1)) loopDates.Add(d);
        }

        // เริ่มวนลูปแสดงผล
        foreach (var date in loopDates)
        {
            var dayShifts = filteredShifts.Where(s => s.FirstScan.Date == date).ToList();

            // จัดเรียงเวลาในแต่ละวันตามเงื่อนไขเดียวกัน
            dayShifts = sortAscending
                ? dayShifts.OrderBy(s => s.FirstScan).ToList()
                : dayShifts.OrderByDescending(s => s.FirstScan).ToList();

            if (dayShifts.Any())
            {
                foreach (var shift in dayShifts)
                {
                    results.Add(FormatShift(shift, layoutStyleIndex));
                }
            }
            else
            {
                bool isCoveredByOvernight = filteredShifts.Any(s => s.FirstScan.Date < date && s.LastScan.Date >= date);

                if (!isCoveredByOvernight)
                {
                    results.Add($"❌ วันที่ `{date:dd/MM/yyyy}` ไม่พบข้อมูลบันทึกเวลาเข้า-ออกงาน");
                }
            }
        }

        // รวมข้อความทั้งหมดคั่นด้วยการขึ้นบรรทัดใหม่
        string shiftHistorySection = string.Join("\n", results);

        // 4 โครงสร้างบริบทใหญ่สำหรับแสดงข้อมูลรายการ (Pure Body Payload)
        string[] filledBodyFormats = [
            $"""
            📅 **สรุปประวัติบันทึกเวลาทำงาน ({dateMsg})**
            {shiftHistorySection}
            """,
            $"""
            📊 **รายงานสรุปข้อมูล Time Attendance ({dateMsg})**
            » รายชื่อรายการบันทึกที่พบในระบบ:
            {shiftHistorySection}
            """,
            $"""
            🏃‍♂️ **ไทม์ไลน์บันทึกการเข้าออกงานพนักงาน ({dateMsg})**
            ┌──────────────────────────────────────────┐
            {string.Join("\n", results.Select(r => "│ " + r))}
            └──────────────────────────────────────────┘
            """,
            $"""
            ⏱️ **ข้อมูลการสแกนนิ้วและใบหน้าในระบบประจำช่วง: {dateMsg}**
            {shiftHistorySection}
            """
        ];

        return filledBodyFormats[layoutStyleIndex];
    }

    // ── 🎲 ฟังก์ชันจัดรูปแบบกะตามบริบทการสุ่ม (Sub-Layout Styles) ──
    private string FormatShift(ShiftRecord shift, int styleIndex)
    {
        var gapHours = (shift.LastScan - shift.FirstScan).TotalHours;
        bool isSingleScan = gapHours < 1.0;

        if (isSingleScan)
        {
            var t = shift.FirstScan;
            string guessMsg = (t.Hour >= 4 && t.Hour <= 12) ? "กะเช้า/ลืมสแกนออก" : (t.Hour >= 14 && t.Hour <= 22) ? "กะบ่าย/ลืมสแกนออก" : "ลืมสแกนคู่";

            return styleIndex switch
            {
                0 => $"• วันที่ {t:dd/MM/yyyy} พบสแกนเดี่ยวเวลา `{t:HH:mm}` น. 🛑 (แจ้งเตือน: {guessMsg})",
                1 => $"🔹 [{t:dd/MM/yyyy}] -> สแกนครั้งเดียวเฉพาะเวลา `{t:HH:mm}` ⚠️ (อาจลืมลงเวลาออกงาน หรือสแกนฝั่งเดียว)",
                2 => $"❗ **{t:dd/MM/yyyy}** สแกนหลุดคู่ชิ้นงานที่เวลา `{t:HH:mm}` น. [โปรดติดต่อ HR เพื่อเช็คสิทธิ์แก้ไข]",
                _ => $"➔ วันที่ `{t:dd/MM/yyyy}` พบ Log สแกนเวลา `{t:HH:mm}` น. เพียงรายการเดียว 🔍 (คาดว่า {guessMsg})"
            };
        }
        else
        {
            bool isOvernight = shift.FirstScan.Date != shift.LastScan.Date;

            if (isOvernight)
            {
                return styleIndex switch
                {
                    0 => $"• 🌙 **กะทำงานข้ามวัน**: เริ่ม {shift.FirstScan:dd/MM/yyyy} เวลา `{shift.FirstScan:HH:mm}` น. ถึง {shift.LastScan:dd/MM/yyyy} เวลา `{shift.LastScan:HH:mm}` น.",
                    1 => $"🔹 **[กะข้ามคืน]** {shift.FirstScan:dd/MM/yyyy} [{shift.FirstScan:HH:mm}] ➔ {shift.LastScan:dd/MM/yyyy} [{shift.LastScan:HH:mm}]",
                    2 => $"🌌 `{shift.FirstScan:dd/MM/yyyy}` ลงเวลาเข้ากะกลางคืน `{shift.FirstScan:HH:mm}` น. -> ออกเวรวันที่ `{shift.LastScan:dd/MM/yyyy}` เวลา `{shift.LastScan:HH:mm}` น.",
                    _ => $"➔ 🌓 *Shift ข้ามวัน:* เข้างาน `{shift.FirstScan:dd/MM/yyyy} {shift.FirstScan:HH:mm}` | ออกงาน `{shift.LastScan:dd/MM/yyyy} {shift.LastScan:HH:mm}`"
                };
            }
            else
            {
                return styleIndex switch
                {
                    0 => $"• ☀️ วันที่ {shift.FirstScan:dd/MM/yyyy} -> เข้า: `{shift.FirstScan:HH:mm}` น. | ออก: `{shift.LastScan:HH:mm}` น.",
                    1 => $"🔹 **วันที่ {shift.FirstScan:dd/MM/yyyy}** เข้างาน `{shift.FirstScan:HH:mm}` ➔ เลิกงาน `{shift.LastScan:HH:mm}`",
                    2 => $"✅ `{shift.FirstScan:dd/MM/yyyy}` [สแกนเข้า: {shift.FirstScan:HH:mm}] [สแกนออก: {shift.LastScan:HH:mm}] บันทึกสำเร็จ",
                    _ => $"➔ วันที่ `{shift.FirstScan:dd/MM/yyyy}` เวลาปฏิบัติงาน `{shift.FirstScan:HH:mm}` น. ถึง `{shift.LastScan:HH:mm}` น."
                };
            }
        }
    }

    // ── Data Models & Queries ──
    private class ShiftRecord { public DateTime FirstScan { get; set; } public DateTime LastScan { get; set; } }
    private sealed class AttendanceScanRow { public DateTime EventTime { get; set; } public string DeviceAlias { get; set; } = ""; public string Source { get; set; } = ""; }

    private async Task<List<AttendanceScanRow>> QueryScan1Async(string employeeId, string fromYmd, string toYmd)
    {
        const string sql = @"
        SELECT a.event_time AS EventTime, ISNULL(c.dev_alias, a.event_point_name) AS DeviceAlias
        FROM hep_transaction a
        INNER JOIN (
            SELECT a.pin, b.cert_number FROM pers_person a
            LEFT JOIN pers_certificate b ON a.id = b.person_id
        ) b ON a.pin = b.pin
        LEFT JOIN acc_device c ON a.dev_sn = c.sn
        WHERE b.cert_number = @EmployeeId
          AND CONVERT(varchar(8), a.event_time, 112) BETWEEN @FromYmd AND @ToYmd
        ORDER BY a.event_time ASC;";
        try { using var conn = new SqlConnection(_faceScan1Conn); var rows = await conn.QueryAsync<AttendanceScanRow>(sql, new { EmployeeId = employeeId, FromYmd = fromYmd, ToYmd = toYmd }); return rows.Select(r => { r.Source = "FaceScan1"; return r; }).ToList(); }
        catch (Exception ex) { _logger.LogWarning(ex, "FaceScan1 query failed for {EmployeeId}", employeeId); return new List<AttendanceScanRow>(); }
    }

    private async Task<List<AttendanceScanRow>> QueryScan2Async(string employeeId, string fromYmd, string toYmd)
    {
        const string sql = @"
        SELECT DATEADD(second, a.nDateTime, '1970-01-01 00:00:00') AS EventTime, ISNULL(b.sIP, '') AS DeviceAlias
        FROM tb_event_log a
        LEFT JOIN tb_reader b ON a.nReaderIdn = b.nReaderIdn
        INNER JOIN tb_user d ON a.nUserID = d.sUserID
        WHERE (a.nEventIdn IN (23, 32, 39, 151) OR a.nEventIdn BETWEEN 43 AND 79)
          AND d.sUserName = @EmployeeId
          AND CONVERT(varchar(8), DATEADD(second, a.nDateTime, '1970-01-01 00:00:00'), 112) BETWEEN @FromYmd AND @ToYmd
        ORDER BY a.nDateTime ASC;";
        try { using var conn = new SqlConnection(_faceScan2Conn); var rows = await conn.QueryAsync<AttendanceScanRow>(sql, new { EmployeeId = employeeId, FromYmd = fromYmd, ToYmd = toYmd }); return rows.Select(r => { r.Source = "FaceScan2"; return r; }).ToList(); }
        catch (Exception ex) { _logger.LogWarning(ex, "FaceScan2 query failed for {EmployeeId}", employeeId); return new List<AttendanceScanRow>(); }
    }

    private async Task<List<AttendanceScanRow>> QueryScan3Async(string employeeId, string fromDate, string toDate)
    {
        const string sql = @"
        SELECT checktime AS EventTime, ISNULL(b.AreaName, '') AS DeviceAlias
        FROM EmployeeAttendance a
        LEFT JOIN GeoAreas b ON a.AreaID = b.AreaID
        WHERE a.employeeid = @EmployeeId
          AND CAST(checktime AS date) BETWEEN @FromDate AND @ToDate
        ORDER BY checktime ASC;";
        try { using var conn = new SqlConnection(_faceScan3Conn); var rows = await conn.QueryAsync<AttendanceScanRow>(sql, new { EmployeeId = employeeId, FromDate = fromDate, ToDate = toDate }); return rows.Select(r => { r.Source = "FaceScan3"; return r; }).ToList(); }
        catch (Exception ex) { _logger.LogWarning(ex, "FaceScan3 query failed for {EmployeeId}", employeeId); return new List<AttendanceScanRow>(); }
    }
}