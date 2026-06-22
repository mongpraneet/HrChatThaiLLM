using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using HrChatThaiLLM.Server.Services;

namespace HrChatThaiLLM.Server.Controllers;

// ══════════════════════════════════════════════════════════════════════════
//  MedicalChartController
//
//  GET /api/medical-chart?year=2026        → กราฟ 1: Bar วงเงิน/ใช้/เหลือ
//  GET /api/medical-chart/hospital?year=   → กราฟ 2: Pie ยอดแยกโรงพยาบาล
//  GET /api/medical-chart/monthly?year=    → กราฟ 3: Line รายเดือน 2 ปี
//
//  year รองรับ:  ไม่ส่ง = ปีปัจจุบัน (ค.ศ.)
//               2026   = ค.ศ.
//               2569   = พ.ศ. → แปลงเป็น ค.ศ. อัตโนมัติ
//               68/69  = พ.ศ. 2 หลัก → แปลงเป็น ค.ศ. อัตโนมัติ
// ══════════════════════════════════════════════════════════════════════════
[ApiController]
[Route("api/medical-chart")]
public class MedicalChartController : ControllerBase
{
    private readonly ISqlExecutorService _sql;
    private readonly IConfiguration _config;
    private readonly ILogger<MedicalChartController> _logger;

    public MedicalChartController(
        ISqlExecutorService sql,
        IConfiguration config,
        ILogger<MedicalChartController> logger)
    {
        _sql = sql;
        _config = config;
        _logger = logger;
    }

    private string? EmpId => HttpContext.Session.GetString("EmployeeId");

    // ══════════════════════════════════════════════════════════════════════
    //  GET /api/medical-chart?year=2026
    //  กราฟ 1: Bar — วงเงินสิทธิ์ / เบิกแล้ว / คงเหลือ
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet]
    public async Task<IActionResult> GetBalance([FromQuery] string? year = null)
    {
        var empId = EmpId;
        if (string.IsNullOrEmpty(empId)) return Unauthorized();

        int yr = ResolveYear(year);

        try
        {
            var (entitlement, entitlementText, totalUsed) =
                await GetEntitlementAndUsed(empId, yr);

            decimal remaining = Math.Max(entitlement - totalUsed, 0);

            return Ok(new MedicalBalanceDto
            {
                Year = yr,
                BuddhistYear = yr + 543,
                EntitlementText = entitlementText,
                Entitlement = entitlement,
                Used = totalUsed,
                Remaining = remaining
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MedicalChart Balance error for {EmpId}", empId);
            return StatusCode(500, "เกิดข้อผิดพลาดในการดึงข้อมูล");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  GET /api/medical-chart/hospital?year=2026
    //  กราฟ 2: Pie/Doughnut — ยอดแยกสถานพยาบาล + คงเหลือ
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("hospital")]
    public async Task<IActionResult> GetByHospital([FromQuery] string? year = null)
    {
        var empId = EmpId;
        if (string.IsNullOrEmpty(empId)) return Unauthorized();

        int yr = ResolveYear(year);

        try
        {
            var (entitlement, _, totalUsed) = await GetEntitlementAndUsed(empId, yr);
            decimal remaining = Math.Max(entitlement - totalUsed, 0);

            // ดึงยอดแยกโรงพยาบาล
            var t = await ResolveTables(empId);
            var conn = new SqlConnection(_config.GetConnectionString("Medical"));
            await conn.OpenAsync();

            var sql = $@"
                SELECT
                    ISNULL(NULLIF(LTRIM(RTRIM(htds)), ''), 'อื่นๆ') AS Hospital,
                    SUM(amap) AS Amount
                FROM {t.ExpOpd}
                WHERE epid  = @EmpId
                  AND yrbd  = @Year
                GROUP BY LTRIM(RTRIM(htds))
                ORDER BY SUM(amap) DESC";

            var rows = (await conn.QueryAsync<HospitalRow>(sql,
                new { EmpId = empId.Trim(), Year = yr })).ToList();

            await conn.CloseAsync();

            // เพิ่ม "คงเหลือ" เป็น slice สุดท้าย
            var labels = rows.Select(r => r.Hospital).ToList();
            var amounts = rows.Select(r => (double)r.Amount).ToList();
            labels.Add("คงเหลือ");
            amounts.Add((double)remaining);

            return Ok(new MedicalHospitalDto
            {
                Year = yr,
                BuddhistYear = yr + 543,
                Labels = labels.ToArray(),
                Amounts = amounts.ToArray(),
                Entitlement = entitlement,
                TotalUsed = totalUsed,
                Remaining = remaining
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MedicalChart Hospital error for {EmpId}", empId);
            return StatusCode(500, "เกิดข้อผิดพลาดในการดึงข้อมูล");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  GET /api/medical-chart/monthly?year=2026
    //  กราฟ 3: Line — รายเดือน เปรียบเทียบ 2 ปี (year และ year-1)
    // ══════════════════════════════════════════════════════════════════════
    [HttpGet("monthly")]
    public async Task<IActionResult> GetMonthly([FromQuery] string? year = null)
    {
        var empId = EmpId;
        if (string.IsNullOrEmpty(empId)) return Unauthorized();

        int yr = ResolveYear(year);
        int prevYr = yr - 1;

        try
        {
            var t = await ResolveTables(empId);
            var conn = new SqlConnection(_config.GetConnectionString("Medical"));
            await conn.OpenAsync();

            var sql = $@"
                SELECT
                    yrbd  AS Year,
                    MONTH(oddt) AS Month,
                    SUM(amap)   AS Amount
                FROM {t.ExpOpd}
                WHERE epid = @EmpId
                  AND yrbd IN (@Year, @PrevYear)
                GROUP BY yrbd, MONTH(oddt)
                ORDER BY yrbd, MONTH(oddt)";

            var rows = (await conn.QueryAsync<MonthlyRow>(sql,
                new { EmpId = empId.Trim(), Year = yr, PrevYear = prevYr })).ToList();

            await conn.CloseAsync();

            // ── สร้าง labels 12 เดือน ───────────────────────────────────
            var thMonths = new[]
            {
                "ม.ค.","ก.พ.","มี.ค.","เม.ย.","พ.ค.","มิ.ย.",
                "ก.ค.","ส.ค.","ก.ย.","ต.ค.","พ.ย.","ธ.ค."
            };

            var currPts = new double[12];
            var prevPts = new double[12];

            foreach (var r in rows)
            {
                int idx = r.Month - 1;
                if (r.Year == yr) currPts[idx] = (double)r.Amount;
                if (r.Year == prevYr) prevPts[idx] = (double)r.Amount;
            }

            return Ok(new MedicalMonthlyDto
            {
                Year = yr,
                PrevYear = prevYr,
                BuddhistYear = yr + 543,
                PrevBuddhistYear = prevYr + 543,
                Labels = thMonths,
                CurrentYear = currPts,
                PrevYear2 = prevPts
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MedicalChart Monthly error for {EmpId}", empId);
            return StatusCode(500, "เกิดข้อผิดพลาดในการดึงข้อมูล");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Private Helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>แปลงปีทุกรูปแบบเป็น ค.ศ. (Gregorian)</summary>
    private static int ResolveYear(string? input)
    {
        int now = DateTime.Now.Year;
        if (string.IsNullOrWhiteSpace(input)) return now;

        if (!int.TryParse(
            System.Text.RegularExpressions.Regex.Match(input, @"\d+").Value,
            out int raw)) return now;

        // 2 หลัก: 68 → พ.ศ. 2568 → ค.ศ. 2025
        if (raw < 100) raw += 2500;
        // 4 หลัก พ.ศ.: >= 2400 → แปลง
        if (raw >= 2400) raw -= 543;
        return raw;
    }

    /// <summary>ดึง MedicalTables ตาม CMID ของพนักงาน</summary>
    private async Task<MedicalTables> ResolveTables(string empId)
    {
        var r = await _sql.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT CMID FROM EMPDA WHERE EMID = @EmpId",
            new { EmpId = empId.Trim() });

        string cmid = r?.CMID?.ToString()?.ToUpperInvariant() ?? "";
        return cmid switch
        {
            "OA" => new("OA_vemployee", "OA_expopd", "OA_EMPBG", "OA_levbg"),
            "BTS" => new("sup_vemployee", "sup_expopd", "sup_EMPBG", "sup_levbg"),
            _ => new("vemployee", "expopd", "EMPBG", "levbg")
        };
    }

    /// <summary>ดึง entitlement (วงเงิน) และ totalUsed รวม (ใช้ logic เดียวกับ MedicalPlugin)</summary>
    private async Task<(decimal Entitlement, string EntitlementText, decimal TotalUsed)>
        GetEntitlementAndUsed(string empId, int year)
    {
        var t = await ResolveTables(empId);
        using var conn = new SqlConnection(_config.GetConnectionString("Medical"));
        await conn.OpenAsync();

        var empSql = $"SELECT TOP 1 mnt, IPosition FROM {t.EmployeeView} WHERE empno = @EmpId";
        var emp = await conn.QueryFirstOrDefaultAsync<EmployeeMedical>(empSql, new { EmpId = empId.Trim() });

        decimal entitlement = 0m;
        string entitlementText = "ไม่พบข้อมูลสิทธิ";

        if (emp is not null)
        {
            var specialSql = $"SELECT TOP 1 yram FROM {t.EmpBg} WHERE epid = @EmpId AND yrbd = @Year";
            var special = await conn.QueryFirstOrDefaultAsync<SpecialBudget>(specialSql, new
            {
                EmpId = empId.Trim(),
                Year = year
            });

            if (special?.Yram is not null)
            {
                entitlement = special.Yram.Value;
                entitlementText = "วงเงินพิเศษ";
            }
            else
            {
                var levSql = $"SELECT TOP 1 * FROM {t.LevBg} WHERE yrbd = @Year AND lvid = @LevelId";
                var lev = await conn.QueryFirstOrDefaultAsync<LevelBudget>(levSql, new
                {
                    Year = year,
                    LevelId = (emp.IPosition ?? string.Empty).Trim()
                });

                if (lev is not null)
                {
                    (entitlement, entitlementText) = CalculateEntitlement(lev, emp.Mnt);
                }
            }
        }

        var usedSql = $@"
            SELECT ISNULL(SUM(amap), 0) AS Total
            FROM {t.ExpOpd}
            WHERE epid = @EmpId AND yrbd = @Year";
        var usedRow = await conn.QueryFirstOrDefaultAsync<UsedTotalRow>(usedSql,
            new { EmpId = empId.Trim(), Year = year });

        return (entitlement, entitlementText, usedRow?.Total ?? 0m);
    }

    private static (decimal amount, string description) CalculateEntitlement(LevelBudget lev, int mnt)
    {
        if (InRange(mnt, lev.Yrs1, lev.Yre1)) return (lev.Yra1 ?? 0m, lev.Yrd1 ?? "");
        if (InRange(mnt, lev.Yrs2, lev.Yre2)) return (lev.Yra2 ?? 0m, lev.Yrd2 ?? "");
        if (InRange(mnt, lev.Yrs3, lev.Yre3)) return (lev.Yra3 ?? 0m, lev.Yrd3 ?? "");
        if (InRange(mnt, lev.Yrs4, lev.Yre4)) return (lev.Yra4 ?? 0m, lev.Yrd4 ?? "");
        return (0m, lev.Yrd1 ?? "ยังไม่เข้าเกณฑ์สิทธิ");
    }

    private static bool InRange(int value, int? start, int? end)
        => start.HasValue && end.HasValue && value >= start.Value && value <= end.Value;

    // ── Inner records ─────────────────────────────────────────────────────
    private sealed record MedicalTables(
        string EmployeeView, string ExpOpd, string EmpBg, string LevBg);

    private sealed class HospitalRow
    {
        public string Hospital { get; set; } = "";
        public decimal Amount { get; set; }
    }

    private sealed class MonthlyRow
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class EmployeeMedical
    {
        public int Mnt { get; set; }
        public string IPosition { get; set; } = "";
    }

    private sealed class SpecialBudget
    {
        public decimal? Yram { get; set; }
    }

    private sealed class UsedTotalRow
    {
        public decimal Total { get; set; }
    }

    private sealed class LevelBudget
    {
        public string? Yrd1 { get; set; }
        public int? Yrs1 { get; set; }
        public int? Yre1 { get; set; }
        public decimal? Yra1 { get; set; }

        public string? Yrd2 { get; set; }
        public int? Yrs2 { get; set; }
        public int? Yre2 { get; set; }
        public decimal? Yra2 { get; set; }

        public string? Yrd3 { get; set; }
        public int? Yrs3 { get; set; }
        public int? Yre3 { get; set; }
        public decimal? Yra3 { get; set; }

        public string? Yrd4 { get; set; }
        public int? Yrs4 { get; set; }
        public int? Yre4 { get; set; }
        public decimal? Yra4 { get; set; }
    }
}

// ── DTO ───────────────────────────────────────────────────────────────────
public class MedicalBalanceDto
{
    public int Year { get; set; }
    public int BuddhistYear { get; set; }
    public string EntitlementText { get; set; } = "";
    public decimal Entitlement { get; set; }
    public decimal Used { get; set; }
    public decimal Remaining { get; set; }
}

public class MedicalHospitalDto
{
    public int Year { get; set; }
    public int BuddhistYear { get; set; }
    public string[] Labels { get; set; } = [];
    public double[] Amounts { get; set; } = [];
    public decimal Entitlement { get; set; }
    public decimal TotalUsed { get; set; }
    public decimal Remaining { get; set; }
}

public class MedicalMonthlyDto
{
    public int Year { get; set; }
    public int PrevYear { get; set; }
    public int BuddhistYear { get; set; }
    public int PrevBuddhistYear { get; set; }
    public string[] Labels { get; set; } = [];
    public double[] CurrentYear { get; set; } = [];
    public double[] PrevYear2 { get; set; } = [];
}
