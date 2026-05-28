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
        [Description("ประโยคคำถามต้นฉบับจากผู้ใช้ (ต้องส่งมาเสมอเพื่อใช้วิเคราะห์วันที่ เช่น วันที่ 5.5.2026 เข้างานกี่โมง)")] string question = "")
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return "ไม่พบรหัสพนักงาน";

        var normalizedEmployeeId = employeeId.Trim();
        var ask = DateNormalizer.NormalizeThaiDates(question?.Trim() ?? string.Empty);

        var askYesterday = ask.Contains("เมื่อวาน", StringComparison.OrdinalIgnoreCase);
        var askToday = ask.Contains("วันนี้", StringComparison.OrdinalIgnoreCase)
                       || ask.Contains("today", StringComparison.OrdinalIgnoreCase);
        var askLatest = ask.Contains("ล่าสุด", StringComparison.OrdinalIgnoreCase)
                         || ask.Contains("last", StringComparison.OrdinalIgnoreCase);

        // ── 1. กำหนดช่วงวันที่ต้องการจริงๆ (Logical Date Range) ──
        var askDateRange = DateNormalizer.TryExtractDateRange(ask, out var requestedStart, out var requestedEnd);

        DateTime requestedDate = default;
        var askDate = !askDateRange && DateNormalizer.TryExtractDate(ask, out requestedDate);

        DateTime reqStart, reqEnd;
        if (askDateRange)
        {
            reqStart = requestedStart.Date;
            reqEnd = requestedEnd.Date;
        }
        else if (askDate)
        {
            reqEnd = requestedDate.Date;
            reqStart = reqEnd.AddDays(-7);
        }
        else if (askYesterday)
        {
            reqEnd = DateTime.Today.AddDays(-1);
            reqStart = reqEnd.AddDays(-7);
        }
        else if (askToday)
        {
            reqEnd = DateTime.Today;
            reqStart = reqEnd.AddDays(-7);
        }
        else
        {
            reqEnd = DateTime.Today;
            reqStart = reqEnd.AddDays(-7);
        }

        // ── 2. ขยายช่วงเวลา Query Database (+/- 1 วัน) เพื่อรองรับกะข้ามวัน ──
        var dbStart = reqStart.AddDays(-1);
        var dbEnd = reqEnd.AddDays(1);

        var startDateStr = dbStart.ToString("yyyyMMdd");
        var endDateStr = dbEnd.ToString("yyyyMMdd");
        var startDateOnly = dbStart.ToString("yyyy-MM-dd");
        var endDateOnly = dbEnd.ToString("yyyy-MM-dd");

        // ── 3. ดึงข้อมูลจาก 3 Server พร้อมกัน ──
        var scan1Task = QueryScan1Async(normalizedEmployeeId, startDateStr, endDateStr);
        var scan2Task = QueryScan2Async(normalizedEmployeeId, startDateStr, endDateStr);
        var scan3Task = QueryScan3Async(normalizedEmployeeId, startDateOnly, endDateOnly);

        await Task.WhenAll(scan1Task, scan2Task, scan3Task);

        var allScans = scan1Task.Result
            .Concat(scan2Task.Result)
            .Concat(scan3Task.Result)
            .OrderBy(r => r.EventTime)
            .ToList();

        var dateMsg = askDateRange ? $"ช่วงวันที่ {reqStart:dd/MM/yyyy} ถึง {reqEnd:dd/MM/yyyy}" :
                      askDate ? $"ย้อนหลัง 7 วัน นับจากวันที่ {reqEnd:dd/MM/yyyy}" :
                      askYesterday ? $"ย้อนหลัง 7 วัน นับจากเมื่อวาน ({reqEnd:dd/MM/yyyy})" :
                      askToday ? $"ย้อนหลัง 7 วัน นับจากวันนี้ ({reqEnd:dd/MM/yyyy})" :
                      "ย้อนหลัง 7 วัน";

        // 🎲 สุ่มเลือกบริบทกรณี "ไม่พบข้อมูล" (4 บริบท)
        if (allScans.Count == 0)
        {
            string[] emptyBodyFormats = [
                $"⚠️ **รายงานเวลาทำงาน**: ไม่พบข้อมูลบันทึกเวลาเข้าออกงานของรหัสพนักงาน `{normalizedEmployeeId}` สำหรับ{dateMsg} ในระบบค่ะ",
                $"🔍 **ผลการค้นหาเครื่องสแกนใบหน้า**: ยังไม่มีประวัติการบันทึกเวลาเข้างาน ({dateMsg}) ของรหัส `{normalizedEmployeeId}` บนระบบคลาวด์ค่ะ",
                $"""
                ⏱️ **บันทึกเวลาเข้างาน (สิทธิ์พนักงาน: {normalizedEmployeeId})**
                ❌ ไม่พบประวัติการ Log-in หรือข้อมูลสแกนนิ้ว/ใบหน้าในช่วง{dateMsg} ค่ะ
                """,
                $"🛑 *ระบบลงเวลาทำงานแจ้งเตือน:* ไม่พบสัญญาณข้อมูลเวลา (Time Attendance Log) สำหรับ{dateMsg} ของพนักงานรหัส `{normalizedEmployeeId}`"
            ];
            return emptyBodyFormats[Random.Shared.Next(emptyBodyFormats.Length)];
        }

        // ── 4. อัลกอริทึมจับคู่กะ (Shift Pairing Algorithm) ──
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

        // ── 5. กรองเฉพาะกะที่ครอบคลุมช่วงเวลาที่ผู้ใช้ถาม ──
        var filteredShifts = shifts.Where(s =>
            (s.FirstScan.Date >= reqStart && s.FirstScan.Date <= reqEnd) ||
            (s.LastScan.Date >= reqStart && s.LastScan.Date <= reqEnd)
        ).OrderByDescending(s => s.FirstScan).ToList();

        // 🎲 สุ่มเลือกบริบทกรณี "ไม่พบข้อมูลหลังจับคู่กะ"
        if (!filteredShifts.Any() && !askLatest)
        {
            string[] noShiftFormats = [
                $"⚠️ ข้อมูลเครื่องสแกนมีบันทึก แต่ไม่เข้าเงื่อนไขจับคู่กะงาน ({dateMsg}) ของรหัส `{normalizedEmployeeId}` ค่ะ",
                $"🔍 ตรวจเช็คโครงสร้างเวลา {dateMsg} แล้ว ไม่พบช่วงเวลาการทำงานที่สมบูรณ์ของรหัส {normalizedEmployeeId} ค่ะ",
                $"⏱️ *ระบบบันทึกงาน:* รายการสแกนใน{dateMsg} ของรหัส {normalizedEmployeeId} ไม่สามารถจับคู่ออกเป็นกะทำงานปกติได้ค่ะ",
                $"🛑 ไม่พบการจับคู่กะเข้างานข้ามวันหรือกะปกติในช่วง {dateMsg} ของพนักงานรหัส `{normalizedEmployeeId}`"
            ];
            return noShiftFormats[Random.Shared.Next(noShiftFormats.Length)];
        }

        // ════════════════════════════════════════════════════════════════════════
        // 🎲 เริ่มกระบวนการสุ่มฟอร์แทตโครงสร้างเนื้อหา 4 บริบท (4 Layout Contexts)
        // ════════════════════════════════════════════════════════════════════════
        int layoutStyleIndex = Random.Shared.Next(4);

        // กรณีถาม "ล่าสุด" -> สุ่มแสดงผลเฉพาะรายการล่าสุด
        if (askLatest)
        {
            var latest = shifts.LastOrDefault();
            if (latest == null) return "❌ ไม่พบข้อมูลการสแกนเข้าออกงานล่าสุดในระบบค่ะ";

            string[] latestBodyFormats = [
                $"⏱️ **ข้อมูลเวลาเข้าออกงานล่าสุด**\n{FormatShift(latest, layoutStyleIndex, reqEnd)}",
                $"🏃‍♂️ **ประวัติการบันทึกเวลาทำงาน (ล่าสุด)**\n» {FormatShift(latest, layoutStyleIndex, reqEnd)}",
                $"""
                📅 **สถานะการลงเวลาทำงานล่าสุดของคุณ**
                ┌──────────────────────────────────────────┐
                │ {FormatShift(latest, layoutStyleIndex, reqEnd)}
                └──────────────────────────────────────────┘
                """,
                $"🔔 *บันทึกสแกนใบหน้าล่าสุดจากระบบเซิร์ฟเวอร์:* {FormatShift(latest, layoutStyleIndex, reqEnd)}"
            ];
            return latestBodyFormats[Random.Shared.Next(latestBodyFormats.Length)];
        }

        // กรณีปกติ -> นำรายการกะทั้งหมดมาวนลูปจัดรูปแบบตามสไตล์ที่สุ่มได้
        var results = filteredShifts.Select(s => FormatShift(s, layoutStyleIndex, reqEnd));
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
            {string.Join("\n", filteredShifts.Select(s => "│ " + FormatShift(s, layoutStyleIndex, reqEnd)))}
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
    private string FormatShift(ShiftRecord shift, int styleIndex, DateTime targetDate = default)
    {
        var gapHours = (shift.LastScan - shift.FirstScan).TotalHours;
        bool isSingleScan = gapHours < 1.0;
        string result;

        if (isSingleScan)
        {
            var t = shift.FirstScan;
            string guessMsg = (t.Hour >= 4 && t.Hour <= 12) ? "กะเช้า/ลืมสแกนออก" : (t.Hour >= 14 && t.Hour <= 22) ? "กะบ่าย/ลืมสแกนออก" : "ลืมสแกนคู่";

            result = styleIndex switch
            {
                0 => $"• วันที่ {t:dd/MM/yyyy} พบสแกนเดี่ยวเวลา `{t:HH:mm}` น. 🛑 (แจ้งเตือน: {guessMsg})",
                1 => $"🔹 [{t:dd/MM/yyyy}] -> สแกนครั้งเดียวเฉพาะเวลา `{t:HH:mm}` ⚠️ (อาจลืมลงเวลาออกงาน หรือสแกนฝั่งเดียว)",
                2 => $"❗ **{t:dd/MM/yyyy}** สแกนหลุดคู่ชิ้นงานที่เวลา `{t:HH:mm}` น. [โปรดติดต่อ HR เพื่อเช็คสิทธิ์แก้ไข]",
                _ => $"➔ `{t:dd/MM/yyyy}` พบ Log สแกนเวลา `{t:HH:mm}` น. เพียงรายการเดียว 🔍 (คาดว่า {guessMsg})"
            };
        }
        else
        {
            bool isOvernight = shift.FirstScan.Date != shift.LastScan.Date;

            if (isOvernight)
            {
                result = styleIndex switch
                {
                    0 => $"• 🌙 **กะทำงานข้ามวัน**: เริ่ม {shift.FirstScan:dd/MM/yyyy} เวลา `{shift.FirstScan:HH:mm}` น. ถึง {shift.LastScan:dd/MM/yyyy} เวลา `{shift.LastScan:HH:mm}` น.",
                    1 => $"🔹 **[กะข้ามคืน]** {shift.FirstScan:dd/MM/yyyy} [{shift.FirstScan:HH:mm}] ➔ {shift.LastScan:dd/MM/yyyy} [{shift.LastScan:HH:mm}]",
                    2 => $"🌌 `{shift.FirstScan:dd/MM/yyyy}` ลงเวลาเข้ากะกลางคืน `{shift.FirstScan:HH:mm}` น. -> ออกเวรวันที่ `{shift.LastScan:dd/MM/yyyy}` เวลา `{shift.LastScan:HH:mm}` น.",
                    _ => $"➔ 🌓 *Shift ข้ามวัน:* เข้างาน `{shift.FirstScan:dd/MM/yyyy} {shift.FirstScan:HH:mm}` | ออกงาน `{shift.LastScan:dd/MM/yyyy} {shift.LastScan:HH:mm}`"
                };
            }
            else
            {
                result = styleIndex switch
                {
                    0 => $"• ☀️ วันที่ {shift.FirstScan:dd/MM/yyyy} -> เข้า: `{shift.FirstScan:HH:mm}` น. | ออก: `{shift.LastScan:HH:mm}` น.",
                    1 => $"🔹 **วันที่ {shift.FirstScan:dd/MM/yyyy}** เข้างาน `{shift.FirstScan:HH:mm}` ➔ เลิกงาน `{shift.LastScan:HH:mm}`",
                    2 => $"✅ `{shift.FirstScan:dd/MM/yyyy}` [สแกนเข้า: {shift.FirstScan:HH:mm}] [สแกนออก: {shift.LastScan:HH:mm}] บันทึกสำเร็จ",
                    _ => $"➔ วันที่ `{shift.FirstScan:dd/MM/yyyy}` เวลาปฏิบัติงาน `{shift.FirstScan:HH:mm}` น. ถึง `{shift.LastScan:HH:mm}` น."
                };
            }
        }

        if (targetDate != default && (shift.FirstScan.Date == targetDate.Date || shift.LastScan.Date == targetDate.Date))
        {
            result = $"### 🎯 {result}";
        }

        return result;
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