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
            var scans = await QueryAllScans(empId.Trim(), from, to);
            var daily = BuildDaySummary(scans);

            int workedDays = daily.Count(d => d.ScanCount >= 2);
            int singleScanDays = daily.Count(d => d.ScanCount == 1);
            int noScanDays = Math.Max(DateTime.IsLeapYear(yr) ? 366 : 365 - daily.Count, 0);

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
            var scans = await QueryAllScans(empId.Trim(), from, to);
            var grouped = scans
                .GroupBy(s => s.Source)
                .OrderByDescending(g => g.Count())
                .ToList();

            return Ok(new AttendanceSourceDto
            {
                Year = yr,
                BuddhistYear = yr + 543,
                Labels = grouped.Select(g => g.Key).ToArray(),
                Amounts = grouped.Select(g => (double)g.Count()).ToArray(),
                Total = scans.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attendance source failed for {EmpId}", empId);
            return StatusCode(500, "โหลดข้อมูลกราฟเวลาเข้าออกงานไม่สำเร็จ");
        }
    }

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
            var scans = await QueryAllScans(empId.Trim(), from, to);
            var daily = BuildDaySummary(scans);

            var curr = new double[12];
            var prev = new double[12];

            foreach (var day in daily.Where(d => d.ScanCount >= 1))
            {
                int idx = day.Date.Month - 1;
                if (day.Date.Year == yr) curr[idx] += 1;
                if (day.Date.Year == prevYr) prev[idx] += 1;
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

    private async Task<List<AttendanceScanRow>> QueryAllScans(string employeeId, DateTime from, DateTime to)
    {
        var ymdFrom = from.ToString("yyyyMMdd");
        var ymdTo = to.ToString("yyyyMMdd");
        var dateFrom = from.ToString("yyyy-MM-dd");
        var dateTo = to.ToString("yyyy-MM-dd");

        var s1 = QueryScan1Async(employeeId, ymdFrom, ymdTo);
        var s2 = QueryScan2Async(employeeId, ymdFrom, ymdTo);
        var s3 = QueryScan3Async(employeeId, dateFrom, dateTo);
        await Task.WhenAll(s1, s2, s3);

        return s1.Result.Concat(s2.Result).Concat(s3.Result)
            .OrderBy(s => s.EventTime)
            .ToList();
    }

    private static List<AttendanceDayRow> BuildDaySummary(List<AttendanceScanRow> scans)
    {
        return scans
            .GroupBy(s => s.EventTime.Date)
            .Select(g => new AttendanceDayRow
            {
                Date = g.Key,
                ScanCount = g.Count(),
                FirstIn = g.Min(x => x.EventTime),
                LastOut = g.Max(x => x.EventTime)
            })
            .OrderBy(g => g.Date)
            .ToList();
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
            var rows = await conn.QueryAsync<AttendanceScanRow>(sql, new { EmployeeId = employeeId, FromYmd = fromYmd, ToYmd = toYmd });
            return rows.Select(r => { r.Source = "FaceScan1"; return r; }).ToList();
        }
        catch
        {
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
          AND CONVERT(varchar(8), DATEADD(second, a.nDateTime, '1970-01-01 00:00:00'), 112) BETWEEN @FromYmd AND @ToYmd
        ORDER BY a.nDateTime ASC;";
        try
        {
            using var conn = new SqlConnection(_faceScan2Conn);
            var rows = await conn.QueryAsync<AttendanceScanRow>(sql, new { EmployeeId = employeeId, FromYmd = fromYmd, ToYmd = toYmd });
            return rows.Select(r => { r.Source = "FaceScan2"; return r; }).ToList();
        }
        catch
        {
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
            var rows = await conn.QueryAsync<AttendanceScanRow>(sql, new { EmployeeId = employeeId, FromDate = fromDate, ToDate = toDate });
            return rows.Select(r => { r.Source = "FaceScan3"; return r; }).ToList();
        }
        catch
        {
            return new List<AttendanceScanRow>();
        }
    }

    private sealed class AttendanceScanRow
    {
        public DateTime EventTime { get; set; }
        public string Source { get; set; } = "";
    }

    private sealed class AttendanceDayRow
    {
        public DateTime Date { get; set; }
        public int ScanCount { get; set; }
        public DateTime FirstIn { get; set; }
        public DateTime LastOut { get; set; }
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
