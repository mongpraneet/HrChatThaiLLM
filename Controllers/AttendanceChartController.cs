using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace HrChatThaiLLM.Server.Controllers;

[ApiController]
[Route("api/attendance-chart")]
public class AttendanceChartController : ControllerBase
{
    private readonly string _faceScan1Conn;
    private readonly string _faceScan2Conn;
    private readonly string _faceScan3Conn;
    private readonly ILogger<AttendanceChartController> _logger;

    // ── threshold เดียวกับ AttendancePlugin (16 ชั่วโมง) ──
    private const double ShiftMaxHours = 16.0;

    public AttendanceChartController(IConfiguration config, ILogger<AttendanceChartController> logger)
    {
        _logger = logger;
        _faceScan1Conn = config.GetConnectionString("FaceScan1DB")
            ?? throw new InvalidOperationException("FaceScan1DB connection string not found");
        _faceScan2Conn = config.GetConnectionString("FaceScan2DB")
            ?? throw new InvalidOperationException("FaceScan2DB connection string not found");
        _faceScan3Conn = config.GetConnectionString("FaceScan3DB")
            ?? throw new InvalidOperationException("FaceScan3DB connection string not found");
    }

    private string? EmpId => HttpContext.Session.GetString("EmployeeId");

    // ══════════════════════════════════════════════════════════════
    // GET /api/attendance-chart?year=2567
    // ══════════════════════════════════════════════════════════════
    [HttpGet]
    public async Task<IActionResult> GetSummary([FromQuery] string? year = null)
    {
        var empId = EmpId;
        if (string.IsNullOrWhiteSpace(empId)) return Unauthorized();

        int yr = ResolveYear(year);
        var from = new DateTime(yr, 1, 1);
        var to = new DateTime(yr, 12, 31, 23, 59, 59);

        try
        {
            // ✅ ขยาย ±1 วัน เพื่อดักสแกนข้ามวันที่ขอบปี
            var scans = await QueryAllScans(empId.Trim(), from.AddDays(-1), to.AddDays(1));
            var shifts = BuildShifts(scans);

            // กรองเฉพาะกะที่ "เริ่มต้น" ในปีที่ขอ (นับตาม FirstScan)
            var shiftsInYear = shifts.Where(s => s.FirstScan.Year == yr).ToList();

            // แยกนับยอดมาทำงาน และ สแกนครั้งเดียว เพื่อเอาไปเข้าสูตร
            int workedDays = shiftsInYear.Count(s => s.ScanCount >= 2);
            int singleScanDays = shiftsInYear.Count(s => s.ScanCount == 1);

            // หาจำนวนวันทั้งหมดในปีจริง (365 หรือ 366 วัน)
            int totalDays = DateTime.IsLeapYear(yr) ? 366 : 365;

            // 🔥 [สูตรตามสั่ง] วันไม่พบสแกน = จำนวนวันในปี - (มาทำงาน + สแกนครั้งเดียว)
            int noScanDays = totalDays - (workedDays + singleScanDays);

            // ป้องกันกรณีเผื่อไว้ (เช่น ข้อมูลสแกนซ้อนจนเกินจำนวนวันในปี ให้ล็อกต่ำสุดที่ 0)
            if (noScanDays < 0) noScanDays = 0;

            _logger.LogInformation(
                "Summary {EmpId} yr={Year}: worked={Worked} single={Single} noScan={NoScan} totalDays={TD}",
                empId, yr, workedDays, singleScanDays, noScanDays, totalDays);

            return Ok(new AttendanceSummaryDto
            {
                Year = yr,
                BuddhistYear = yr + 543,
                WorkedDays = workedDays,
                SingleScanDays = singleScanDays,
                NoScanDays = noScanDays,
                TotalScans = scans.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attendance summary failed for {EmpId}", empId);
            return StatusCode(500, "โหลดข้อมูลกราฟเวลาเข้าออกงานไม่สำเร็จ");
        }
    }

    // ══════════════════════════════════════════════════════════════
    // GET /api/attendance-chart/source?year=2567
    // ══════════════════════════════════════════════════════════════
    [HttpGet("source")]
    public async Task<IActionResult> GetBySource([FromQuery] string? year = null)
    {
        var empId = EmpId;
        if (string.IsNullOrWhiteSpace(empId)) return Unauthorized();

        int yr = ResolveYear(year);
        var from = new DateTime(yr, 1, 1);
        var to = new DateTime(yr, 12, 31, 23, 59, 59);

        try
        {
            var scans = await QueryAllScans(empId.Trim(), from.AddDays(-1), to.AddDays(1));
            var inYear = scans.Where(s => s.EventTime >= from && s.EventTime <= to).ToList();
            var grouped = inYear
                .GroupBy(s => s.Source)
                .OrderByDescending(g => g.Count())
                .ToList();

            return Ok(new AttendanceSourceDto
            {
                Year = yr,
                BuddhistYear = yr + 543,
                Labels = grouped.Select(g => g.Key).ToArray(),
                Amounts = grouped.Select(g => (double)g.Count()).ToArray(),
                Total = inYear.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attendance source failed for {EmpId}", empId);
            return StatusCode(500, "โหลดข้อมูลกราฟเวลาเข้าออกงานไม่สำเร็จ");
        }
    }

    // ══════════════════════════════════════════════════════════════
    // GET /api/attendance-chart/monthly?year=2567
    // ══════════════════════════════════════════════════════════════
    [HttpGet("monthly")]
    public async Task<IActionResult> GetMonthly([FromQuery] string? year = null)
    {
        var empId = EmpId;
        if (string.IsNullOrWhiteSpace(empId)) return Unauthorized();

        int yr = ResolveYear(year);
        int prevYr = yr - 1;
        var from = new DateTime(prevYr, 1, 1);
        var to = new DateTime(yr, 12, 31, 23, 59, 59);

        try
        {
            var scans = await QueryAllScans(empId.Trim(), from.AddDays(-1), to.AddDays(1));
            var shifts = BuildShifts(scans);

            var curr = new double[12];
            var prev = new double[12];

            foreach (var s in shifts)
            {
                int idx = s.FirstScan.Month - 1;
                if (s.FirstScan.Year == yr) curr[idx] += 1;
                if (s.FirstScan.Year == prevYr) prev[idx] += 1;
            }

            return Ok(new AttendanceMonthlyDto
            {
                Year = yr,
                PrevYear = prevYr,
                BuddhistYear = yr + 543,
                PrevBuddhistYear = prevYr + 543,
                Labels = new[] { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." },
                CurrentYear = curr,
                PrevYear2 = prev
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attendance monthly failed for {EmpId}", empId);
            return StatusCode(500, "โหลดข้อมูลกราฟเวลาเข้าออกงานไม่สำเร็จ");
        }
    }

    // ── อัลกอริทึมจับคู่กะ ──
    private static List<ShiftRecord> BuildShifts(List<AttendanceScanRow> scans)
    {
        var shifts = new List<ShiftRecord>();
        ShiftRecord? current = null;

        foreach (var scan in scans.OrderBy(s => s.EventTime))
        {
            if (current == null)
            {
                current = new ShiftRecord { FirstScan = scan.EventTime, LastScan = scan.EventTime, ScanCount = 1 };
            }
            else
            {
                var gap = (scan.EventTime - current.FirstScan).TotalHours;
                if (gap <= ShiftMaxHours)
                {
                    current.LastScan = scan.EventTime;
                    current.ScanCount++;
                }
                else
                {
                    shifts.Add(current);
                    current = new ShiftRecord { FirstScan = scan.EventTime, LastScan = scan.EventTime, ScanCount = 1 };
                }
            }
        }
        if (current != null) shifts.Add(current);

        return shifts;
    }

    // ── แปลงปี พ.ศ. / ค.ศ. ──
    private static int ResolveYear(string? input)
    {
        int now = DateTime.Now.Year;
        if (string.IsNullOrWhiteSpace(input)) return now;
        var m = System.Text.RegularExpressions.Regex.Match(input, @"\d+");
        if (!m.Success || !int.TryParse(m.Value, out int raw)) return now;
        if (raw < 100) raw += 2500;
        if (raw >= 2400) raw -= 543;
        return raw;
    }

    // ── Query ทั้ง 3 server ──
    private async Task<List<AttendanceScanRow>> QueryAllScans(string employeeId, DateTime from, DateTime to)
    {
        var ymdFrom = from.ToString("yyyyMMdd");
        var ymdTo = to.ToString("yyyyMMdd");
        var dateFrom = from.ToString("yyyy-MM-dd");
        var dateTo = to.ToString("yyyy-MM-dd");

        var t1 = QueryScan1Async(employeeId, ymdFrom, ymdTo);
        var t2 = QueryScan2Async(employeeId, ymdFrom, ymdTo);
        var t3 = QueryScan3Async(employeeId, dateFrom, dateTo);
        await Task.WhenAll(t1, t2, t3);

        var all = t1.Result.Concat(t2.Result).Concat(t3.Result)
                    .OrderBy(s => s.EventTime)
                    .ToList();

        _logger.LogInformation(
            "QueryAllScans {EmpId} {From}~{To}: S1={C1} S2={C2} S3={C3} Total={Tot}",
            employeeId, ymdFrom, ymdTo, t1.Result.Count, t2.Result.Count, t3.Result.Count, all.Count);

        return all;
    }

    private async Task<List<AttendanceScanRow>> QueryScan1Async(string employeeId, string fromYmd, string toYmd)
    {
        const string sql = @"
        SELECT a.event_time AS EventTime
        FROM hep_transaction a
        INNER JOIN (
            SELECT a.pin, b.cert_number FROM pers_person a
            LEFT JOIN pers_certificate b ON a.id = b.person_id
        ) b ON a.pin = b.pin
        WHERE b.cert_number = @EmployeeId
          AND CONVERT(varchar(8), a.event_time, 112) BETWEEN @FromYmd AND @ToYmd
        ORDER BY a.event_time ASC;";
        try
        {
            using var conn = new SqlConnection(_faceScan1Conn);
            var rows = await conn.QueryAsync<AttendanceScanRow>(
                sql, new { EmployeeId = employeeId, FromYmd = fromYmd, ToYmd = toYmd });
            return rows.Select(r => { r.Source = "FaceScan1"; return r; }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryScan1 failed for {EmpId}", employeeId);
            return new List<AttendanceScanRow>();
        }
    }

    private async Task<List<AttendanceScanRow>> QueryScan2Async(string employeeId, string fromYmd, string toYmd)
    {
        const string sql = @"
        SELECT DATEADD(second, a.nDateTime, '1970-01-01 00:00:00') AS EventTime
        FROM tb_event_log a
        INNER JOIN tb_user d ON a.nUserID = d.sUserID
        WHERE (a.nEventIdn IN (23, 32, 39, 151) OR a.nEventIdn BETWEEN 43 AND 79)
          AND d.sUserName = @EmployeeId
          AND CONVERT(varchar(8), DATEADD(second, a.nDateTime, '1970-01-01 00:00:00'), 112)
              BETWEEN @FromYmd AND @ToYmd
        ORDER BY a.nDateTime ASC;";
        try
        {
            using var conn = new SqlConnection(_faceScan2Conn);
            var rows = await conn.QueryAsync<AttendanceScanRow>(
                sql, new { EmployeeId = employeeId, FromYmd = fromYmd, ToYmd = toYmd });
            return rows.Select(r => { r.Source = "FaceScan2"; return r; }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryScan2 failed for {EmpId}", employeeId);
            return new List<AttendanceScanRow>();
        }
    }

    private async Task<List<AttendanceScanRow>> QueryScan3Async(string employeeId, string fromDate, string toDate)
    {
        const string sql = @"
        SELECT checktime AS EventTime
        FROM EmployeeAttendance a
        WHERE a.employeeid = @EmployeeId
          AND CAST(checktime AS date) BETWEEN @FromDate AND @ToDate
        ORDER BY checktime ASC;";
        try
        {
            using var conn = new SqlConnection(_faceScan3Conn);
            var rows = await conn.QueryAsync<AttendanceScanRow>(
                sql, new { EmployeeId = employeeId, FromDate = fromDate, ToDate = toDate });
            return rows.Select(r => { r.Source = "FaceScan3"; return r; }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryScan3 failed for {EmpId}", employeeId);
            return new List<AttendanceScanRow>();
        }
    }

    private sealed class AttendanceScanRow
    {
        public DateTime EventTime { get; set; }
        public string Source { get; set; } = "";
    }

    private sealed class ShiftRecord
    {
        public DateTime FirstScan { get; set; }
        public DateTime LastScan { get; set; }
        public int ScanCount { get; set; }
        public bool IsOvernight => FirstScan.Date != LastScan.Date;
    }
}

public class AttendanceSummaryDto
{
    public int Year { get; set; }
    public int BuddhistYear { get; set; }
    public int WorkedDays { get; set; }
    public int SingleScanDays { get; set; }
    public int NoScanDays { get; set; }
    public int TotalScans { get; set; }
}

public class AttendanceSourceDto
{
    public int Year { get; set; }
    public int BuddhistYear { get; set; }
    public string[] Labels { get; set; } = [];
    public double[] Amounts { get; set; } = [];
    public int Total { get; set; }
}

public class AttendanceMonthlyDto
{
    public int Year { get; set; }
    public int PrevYear { get; set; }
    public int BuddhistYear { get; set; }
    public int PrevBuddhistYear { get; set; }
    public string[] Labels { get; set; } = [];
    public double[] CurrentYear { get; set; } = [];
    public double[] PrevYear2 { get; set; } = [];
}