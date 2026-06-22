using Microsoft.AspNetCore.Mvc;
using System.Data;
using System.Data.OleDb;

namespace HrChatThaiLLM.Server.Controllers;

[ApiController]
[Route("api/training-chart")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class TrainingChartController : ControllerBase
{
    private readonly string _trainingConn;
    private readonly ILogger<TrainingChartController> _logger;

    public TrainingChartController(IConfiguration config, ILogger<TrainingChartController> logger)
    {
        _logger = logger;
        _trainingConn = config.GetConnectionString("Training")
            ?? throw new InvalidOperationException("Training connection string not found");
    }

    private string? EmpId => HttpContext.Session.GetString("EmployeeId");

    // GET /api/training-chart/overview?year=2026
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] string? year = null)
    {
        var yrCandidates = ResolveYearCandidates(year);
        try
        {
            var rows = await QueryClasses(yrCandidates);
            
            var monthlyCourses = new int[12];
            var internalCourses = new int[12];
            var externalCourses = new int[12];

            foreach(var r in rows)
            {
                if(r.StartTime.HasValue)
                {
                    int mIdx = r.StartTime.Value.Month - 1;
                    monthlyCourses[mIdx]++;
                    if(r.Type == "ภายนอก") externalCourses[mIdx]++;
                    else internalCourses[mIdx]++;
                }
            }

            int gregorianYear = yrCandidates.Select(y => int.TryParse(y, out var i) ? i : 0).FirstOrDefault(y => y >= 1900 && y < 2400);
            if(gregorianYear == 0) gregorianYear = DateTime.Now.Year;

            return Ok(new TrainingOverviewDto
            {
                Year = gregorianYear,
                BuddhistYear = gregorianYear + 543,
                MonthlyCourses = monthlyCourses,
                InternalCourses = internalCourses,
                ExternalCourses = externalCourses,
                Labels = new[] { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Training chart overview error");
            return StatusCode(500, "เกิดข้อผิดพลาดในการดึงข้อมูล");
        }
    }

    // GET /api/training-chart/employee?year=2026
    [HttpGet("employee")]
    public async Task<IActionResult> GetEmployeeSpecific([FromQuery] string? year = null)
    {
        var empId = EmpId;
        if (string.IsNullOrEmpty(empId)) return Unauthorized();

        var yrCandidates = ResolveYearCandidates(year);
        try
        {
            var rows = await QueryEmployeeTraining(empId, yrCandidates);
            
            var monthlyCourses = new int[12];
            var monthlyHours = new double[12];

            foreach(var r in rows)
            {
                if(r.StartTime.HasValue)
                {
                    int mIdx = r.StartTime.Value.Month - 1;
                    monthlyCourses[mIdx]++;

                    if(r.EndTime.HasValue && r.EndTime.Value > r.StartTime.Value)
                    {
                        monthlyHours[mIdx] += (r.EndTime.Value - r.StartTime.Value).TotalHours;
                    }
                }
            }

            int gregorianYear = yrCandidates.Select(y => int.TryParse(y, out var i) ? i : 0).FirstOrDefault(y => y >= 1900 && y < 2400);
            if(gregorianYear == 0) gregorianYear = DateTime.Now.Year;

            return Ok(new TrainingEmployeeDto
            {
                Year = gregorianYear,
                BuddhistYear = gregorianYear + 543,
                MonthlyCourses = monthlyCourses,
                MonthlyHours = monthlyHours,
                Labels = new[] { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." }
            });
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Training chart employee error for {EmpId}", empId);
            return StatusCode(500, "เกิดข้อผิดพลาดในการดึงข้อมูล");
        }
    }

    private List<string> ResolveYearCandidates(string? input)
    {
        int now = DateTime.Now.Year;
        if (string.IsNullOrWhiteSpace(input)) return BuildYearCandidates(now);

        var match = System.Text.RegularExpressions.Regex.Match(input, @"\d+");
        if (!match.Success || !int.TryParse(match.Value, out int raw)) return BuildYearCandidates(now);

        return BuildYearCandidates(raw);
    }

    private static List<string> BuildYearCandidates(int rawYear)
    {
        int gregorianYear = rawYear;
        if (rawYear >= 0 && rawYear <= 99) gregorianYear = rawYear + 2500 - 543;
        else if (rawYear >= 2400) gregorianYear = rawYear - 543;

        int buddhistYear = gregorianYear + 543;

        var candidates = new List<string>
        {
            buddhistYear.ToString(),
            gregorianYear.ToString()
        };

        if (rawYear is >= 0 and <= 99)
        {
            candidates.Add(rawYear.ToString("00"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<TrainingRow>> QueryClasses(List<string> years)
    {
        if (years.Count == 0) return new List<TrainingRow>();
        
        var yearPlaceholders = string.Join(", ", years.Select(_ => "?"));
        var sql = $@"
            SELECT
                c.ClassId, c.CRelease, c.TrYear, c.Type,
                MIN(tt.Stime) AS StartTime,
                MAX(tt.Etime) AS EndTime
            FROM TRClass c
            LEFT JOIN TRTrainingtime tt
                ON tt.ClassId = c.ClassId AND tt.CRelease = c.CRelease AND tt.TrYear = c.TrYear
            WHERE c.TrYear IN ({yearPlaceholders})
            GROUP BY c.ClassId, c.CRelease, c.TrYear, c.Type
        ";

        using var conn = new OleDbConnection(_trainingConn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;

        foreach (var year in years)
        {
            cmd.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.VarWChar, Value = year });
        }

        await conn.OpenAsync();
        using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<TrainingRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new TrainingRow
            {
                Type = reader["Type"] == DBNull.Value ? null : Convert.ToString(reader["Type"])?.Trim(),
                StartTime = reader["StartTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["StartTime"]),
                EndTime = reader["EndTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["EndTime"])
            });
        }
        return rows;
    }

    private async Task<List<TrainingRow>> QueryEmployeeTraining(string employeeId, List<string> years)
    {
        if (years.Count == 0) return new List<TrainingRow>();
        
        var yearPlaceholders = string.Join(", ", years.Select(_ => "?"));
        var sql = $@"
            SELECT
                c.ClassId, c.CRelease, c.TrYear, c.Type,
                MIN(tt.Stime) AS StartTime,
                MAX(tt.Etime) AS EndTime,
                MAX(tr.TrAccess) AS TrAccess
            FROM TRTraining tr
            INNER JOIN TRClass c
                ON c.ClassId = tr.ClassId AND c.CRelease = tr.CRelease AND c.TrYear = tr.TrYear
            LEFT JOIN TRTrainingtime tt
                ON tt.ClassId = tr.ClassId AND tt.CRelease = tr.CRelease AND tt.TrYear = tr.TrYear
            WHERE tr.EmpNo = ? AND tr.TrYear IN ({yearPlaceholders})
            GROUP BY c.ClassId, c.CRelease, c.TrYear, c.Type
        ";

        using var conn = new OleDbConnection(_trainingConn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.VarWChar, Value = employeeId.Trim() });

        foreach (var year in years)
        {
            cmd.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.VarWChar, Value = year });
        }

        await conn.OpenAsync();
        using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<TrainingRow>();
        while (await reader.ReadAsync())
        {
            var access = reader["TrAccess"] == DBNull.Value ? null : Convert.ToString(reader["TrAccess"])?.Trim();
            if(!string.IsNullOrEmpty(access) && access.Contains("ไม่", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Skip unattended
            }

            rows.Add(new TrainingRow
            {
                Type = reader["Type"] == DBNull.Value ? null : Convert.ToString(reader["Type"])?.Trim(),
                StartTime = reader["StartTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["StartTime"]),
                EndTime = reader["EndTime"] == DBNull.Value ? null : Convert.ToDateTime(reader["EndTime"])
            });
        }
        return rows;
    }

    private class TrainingRow
    {
        public string? Type { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }
}

public class TrainingOverviewDto
{
    public int Year { get; set; }
    public int BuddhistYear { get; set; }
    public string[] Labels { get; set; } = [];
    public int[] MonthlyCourses { get; set; } = [];
    public int[] InternalCourses { get; set; } = [];
    public int[] ExternalCourses { get; set; } = [];
}

public class TrainingEmployeeDto
{
    public int Year { get; set; }
    public int BuddhistYear { get; set; }
    public string[] Labels { get; set; } = [];
    public int[] MonthlyCourses { get; set; } = [];
    public double[] MonthlyHours { get; set; } = [];
}
