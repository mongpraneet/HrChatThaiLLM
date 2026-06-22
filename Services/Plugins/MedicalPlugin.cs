using System.ComponentModel;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.SemanticKernel;

namespace HrChatThaiLLM.Server.Services.Plugins;

public class MedicalPlugin
{
    private readonly ISqlExecutorService _sql;
    private readonly string _medicalConn;
    private readonly ILogger<MedicalPlugin> _logger;

    public MedicalPlugin(ISqlExecutorService sql, IConfiguration config, ILogger<MedicalPlugin> logger)
    {
        _sql = sql;
        _logger = logger;
        _medicalConn = config.GetConnectionString("Medical")
            ?? throw new InvalidOperationException("Medical connection string not found");
    }

    [KernelFunction("get_claim_status")]
    [Description("ตรวจสอบสิทธิค่ารักษา ยอดคงเหลือ และประวัติการรักษาพยาบาลของพนักงาน")]
    public async Task<string> GetClaimStatus(
        [Description("รหัสพนักงาน")] string employeeId,
        [Description("คำถามจากผู้ใช้ เช่น ปีนี้, 2569, 2026, 69")] string question = "",
        [Description("ปีที่ระบุเพิ่ม ถ้าไม่ส่งจะอ่านจากคำถาม")] string? yearInput = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return "ไม่พบรหัสพนักงาน";

        try
        {
            var year = ResolveGregorianYear(question, yearInput);
            var profile = await _sql.QueryFirstOrDefaultAsync<EmployeeContext>(
                "SELECT CMID, EMID, DPID, EMFNT AS FirstName, EMLNT AS LastName FROM EMPDA WHERE EMID = @EmployeeId",
                new { EmployeeId = employeeId.Trim() });

            if (profile == null || string.IsNullOrWhiteSpace(profile.CMID))
                return $"ไม่พบข้อมูลพนักงาน {employeeId}";

            var t = ResolveTables(profile.CMID);
            using var conn = new SqlConnection(_medicalConn);

            var empSql = $"SELECT TOP 1 mnt, IPosition FROM {t.EmployeeView} WHERE empno = @EmployeeId";
            var emp = await conn.QueryFirstOrDefaultAsync<EmployeeMedical>(empSql, new { EmployeeId = employeeId.Trim() });
            if (emp == null)
                return $"ไม่พบข้อมูลสิทธิค่ารักษาในบริษัท {profile.CMID}";

            var specialSql = $"SELECT TOP 1 yram FROM {t.EmpBg} WHERE epid = @EmployeeId AND yrbd = @Year";
            var special = await conn.QueryFirstOrDefaultAsync<SpecialBudget>(specialSql, new
            {
                EmployeeId = employeeId.Trim(),
                Year = year
            });

            decimal entitlement = 0m;
            string entitlementText = "ไม่พบข้อมูลสิทธิ";

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
                    LevelId = emp.IPosition
                });

                if (lev != null)
                {
                    (entitlement, entitlementText) = CalculateEntitlement(lev, emp.Mnt);
                }
            }

            var opdSql = $"""
                SELECT oddt AS VisitDate, htds AS Hospital, odds AS Detail, amap AS Amount
                FROM {t.ExpOpd}
                WHERE epid = @EmployeeId AND yrbd = @Year
                ORDER BY oddt ASC
                """;
            var visits = (await conn.QueryAsync<OpdVisit>(opdSql, new
            {
                EmployeeId = employeeId.Trim(),
                Year = year
            })).ToList();

            decimal totalUsed = visits.Sum(x => x.Amount);
            decimal remaining = Math.Max(0, entitlement - totalUsed);

            // ════════════════════════════════════════════════════════════════════════
            // 🎲 เริ่มกระบวนการสุ่มฟอร์แมตโครงสร้างเนื้อหา (Body Randomizer Engine)
            // ════════════════════════════════════════════════════════════════════════

            // 1. จัดการกรณี "ไม่มีประวัติการรักษาเลย" ให้สุ่มสไตล์การรายงานผล
            if (visits.Count == 0)
            {
                string[] emptyBodyFormats = [
                    $"""
                    📊 **สรุปสิทธิค่ารักษาพยาบาลปี {year}** ({entitlementText})
                    • วงเงินที่ได้รับ : `{entitlement:N0}` บาท 💵
                    • ยอดใช้จ่ายสะสม : `0` บาท 📉
                    • วงเงินคงเหลือสุทธิ : `{remaining:N0}` บาท 💚
                    • รายการประวัติ : 📋 ยังไม่มีประวัติการเบิกจ่ายในปีนี้ค่ะ
                    """,
                    $"""
                    🏥 **ข้อมูลกองทุนสวัสดิการสุขภาพประจำปี {year}**
                    🔍 สิทธิ์รูปแบบ: {entitlementText}
                    ┌──────────────────────────────┐
                    │  💳 วงเงินประจำปี: {entitlement:N0} บาท
                    │  🩺 ยอดใช้เบิกแล้ว: 0 บาท
                    │  ✨ ยอดคงเหลือสุทธิ: {remaining:N0} บาท
                    └──────────────────────────────┘
                    ℹ️ *หมายเหตุ: ระบบยังไม่พบรายการประวัติการเข้าโรงพยาบาลในปีนี้ของคุณค่ะ*
                    """,
                    $"""
                    🩺 **สิทธิประโยชน์ค่ารักษาพยาบาล (ปี ค.ศ. {year})**
                    » [ประเภทสิทธิ์] : {entitlementText}
                    » [วงเงินจัดสรร] : `{entitlement:N0}` บาท
                    » [ยอดใช้ไปแล้ว] : `0` บาท
                    » [ยอดเงินคงเหลือ] : `{remaining:N0}` บาท 🎉
                    *(พนักงานยังไม่มีประวัติการเข้ารักษาพยาบาลหรือยื่นเคลมค่าใช้จ่ายในระบบ)*
                    """
                ];

                return emptyBodyFormats[Random.Shared.Next(emptyBodyFormats.Length)];
            }

            // 2. จัดการกรณี "มีประวัติการรักษา" -> สุ่มหน้าตาของรายการประวัติ (Line Item Style)
            int lineStyleIndex = Random.Shared.Next(3);
            var visitLines = new List<string>();
            decimal runningBalance = entitlement;

            foreach (var v in visits)
            {
                runningBalance = Math.Max(0, runningBalance - v.Amount);

                string line = lineStyleIndex switch
                {
                    0 => $"• {v.VisitDate:dd/MM/yyyy} | {v.Hospital} ({v.Detail}) | ใช้ `{v.Amount:N0}` บาท | เหลือ `{runningBalance:N0}` บาท",
                    1 => $"🔹 **วันที่ {v.VisitDate:dd/MM/yyyy}** -> {v.Hospital} | ค่ารักษา `{v.Amount:N0}` ฿ [ยอดคงเหลือ: {runningBalance:N0} ฿]",
                    _ => $" 🩺 {v.VisitDate:dd/MM/yyyy} ณ {v.Hospital} (`{v.Detail}`) เบิกจ่ายแล้ว `{v.Amount:N0}` บาท (คงเหลือในระบบ: {runningBalance:N0} บาท)"
                };
                visitLines.Add(line);
            }

            // 3. สุ่มหัวข้อหัวประวัติและสุ่มโครงสร้าง Layout หลักของ Body ทั้งหมด
            string historySection = string.Join("\n", visitLines);

            string[] filledBodyFormats = [
                $"""
                📊 **ข้อมูลวงเงินและการเบิกจ่ายปี {year}** ({entitlementText})
                • วงเงินสิทธิทั้งหมด : `{entitlement:N0}` บาท 💵
                • ยอดใช้สะสมสะสม : `{totalUsed:N0}` บาท 📉
                • วงเงินคงเหลือสุทธิ : `{remaining:N0}` บาท 💚

                📋 **รายละเอียดประวัติการเข้ารับการรักษาพยาบาล:**
                {historySection}
                """,
                $"""
                🏥 **อัปเดตยอดสวัสดิการค่ารักษาพยาบาลของคุณ ({year})**
                🔍 ประเภทสิทธิ์: {entitlementText}
                ┌──────────────────────────────┐
                │  💳 วงเงินจัดสรร: {entitlement:N0} บาท
                │  🩺 ยอดที่ใช้ไป: {totalUsed:N0} บาท
                │  💎 คงเหลือใช้งาน: {remaining:N0} บาท
                └──────────────────────────────┘
                📜 **Timeline ประวัติการยื่นเคลมค่ารักษา:**
                {historySection}
                """,
                $"""
                🩺 **ยอดสรุปสิทธิการรักษาพยาบาลประจำปี ค.ศ. {year}**
                » เกณฑ์สิทธิของคุณ : {entitlementText}
                » วงเงินจัดสรรประจำปี : `{entitlement:N0}` บาท
                » ยอดที่ใช้จ่ายไปแล้ว : `{totalUsed:N0}` บาท 🩹
                » ยอดสิทธิคงเหลือสุทธิ : `{remaining:N0}` บาท ✨

                📝 **บันทึกรายการประวัติการรักษาพยาบาลในระบบ:**
                {historySection}
                """
            ];

            // ส่งคืนค่ารูปแบบสุ่มแบบ Pure Body ไร้คำทักทายสวัสดีหรือคำลงท้ายปิดแชท
            return filledBodyFormats[Random.Shared.Next(filledBodyFormats.Length)];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Medical query failed for {EmployeeId}", employeeId);
            return "❌ ไม่สามารถดึงข้อมูลค่ารักษาพยาบาลได้ในขณะนี้";
        }
    }

    [KernelFunction("get_claim_history")]
    [Description("แสดงประวัติการเบิกค่ารักษาพยาบาลและสรุปยอดคงเหลือ โดยเน้นรายการย้อนหลัง")]
    public async Task<string> GetClaimHistory(
        [Description("รหัสพนักงาน")] string employeeId,
        [Description("คำถามจากผู้ใช้ เช่น ปีนี้, 2569, 2026, 69")] string question = "",
        [Description("ปีที่ระบุเพิ่ม ถ้าไม่ส่งจะอ่านจากคำถาม")] string? yearInput = null)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return "ไม่พบรหัสพนักงาน";

        try
        {
            var year = ResolveGregorianYear(question, yearInput);
            var profile = await _sql.QueryFirstOrDefaultAsync<EmployeeContext>(
                "SELECT CMID, EMID, DPID, EMFNT AS FirstName, EMLNT AS LastName FROM EMPDA WHERE EMID = @EmployeeId",
                new { EmployeeId = employeeId.Trim() });

            if (profile == null || string.IsNullOrWhiteSpace(profile.CMID))
                return $"ไม่พบข้อมูลพนักงาน {employeeId}";

            var t = ResolveTables(profile.CMID);
            using var conn = new SqlConnection(_medicalConn);

            var empSql = $"SELECT TOP 1 mnt, IPosition FROM {t.EmployeeView} WHERE empno = @EmployeeId";
            var emp = await conn.QueryFirstOrDefaultAsync<EmployeeMedical>(empSql, new { EmployeeId = employeeId.Trim() });
            if (emp == null)
                return $"ไม่พบข้อมูลสิทธิค่ารักษาในบริษัท {profile.CMID}";

            var specialSql = $"SELECT TOP 1 yram FROM {t.EmpBg} WHERE epid = @EmployeeId AND yrbd = @Year";
            var special = await conn.QueryFirstOrDefaultAsync<SpecialBudget>(specialSql, new
            {
                EmployeeId = employeeId.Trim(),
                Year = year
            });

            decimal entitlement = 0m;
            if (special?.Yram is not null)
            {
                entitlement = special.Yram.Value;
            }
            else
            {
                var levSql = $"SELECT TOP 1 * FROM {t.LevBg} WHERE yrbd = @Year AND lvid = @LevelId";
                var lev = await conn.QueryFirstOrDefaultAsync<LevelBudget>(levSql, new
                {
                    Year = year,
                    LevelId = emp.IPosition
                });

                if (lev != null)
                {
                    (entitlement, _) = CalculateEntitlement(lev, emp.Mnt);
                }
            }

            var opdSql = $"""
                SELECT oddt AS VisitDate, htds AS Hospital, odds AS Detail, amap AS Amount
                FROM {t.ExpOpd}
                WHERE epid = @EmployeeId AND yrbd = @Year
                ORDER BY oddt ASC
                """;
            var visits = (await conn.QueryAsync<OpdVisit>(opdSql, new
            {
                EmployeeId = employeeId.Trim(),
                Year = year
            })).ToList();

            decimal totalUsed = visits.Sum(x => x.Amount);
            decimal remaining = Math.Max(0, entitlement - totalUsed);

            if (visits.Count == 0)
            {
                return $"""
📊 **ประวัติการใช้สิทธิค่ารักษา (ปี {year})**
ยังไม่พบรายการเบิกค่ารักษาในปีนี้

👉 สรุป: ยอดคงเหลือปัจจุบัน `{remaining:N0}` บาท
""";
            }

            var lines = new List<string>();
            var running = entitlement;
            foreach (var v in visits)
            {
                running = Math.Max(0, running - v.Amount);
                lines.Add($"- {v.VisitDate:dd/MM/yyyy} | {v.Hospital} | ใช้ {v.Amount:N0} บาท | คงเหลือ {running:N0} บาท");
            }

            return $"""
📊 **ประวัติการใช้สิทธิค่ารักษา (ปี {year})**
{string.Join("\n", lines)}

👉 สรุป: ยอดคงเหลือปัจจุบัน `{remaining:N0}` บาท
""";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Medical history query failed for {EmployeeId}", employeeId);
            return "❌ ไม่สามารถดึงประวัติเบิกค่ารักษาได้ในขณะนี้";
        }
    }

    // (ส่วนฟังก์ชัน Helper ย่อยด้านล่าง เช่น ResolveGregorianYear, CalculateEntitlement, ResolveTables คงเดิมไว้ทั้งหมด...)
    private static int ResolveGregorianYear(string question, string? yearInput)
    {
        var now = DateTime.Now.Year;
        var merged = NormalizeThaiDigits($"{yearInput ?? string.Empty} {question ?? string.Empty}");
        var m = System.Text.RegularExpressions.Regex.Match(merged, @"(?<!\d)(\d{2,4})(?!\d)");
        if (!m.Success) return now;
        if (!int.TryParse(m.Groups[1].Value, out var rawYear)) return now;
        return NormalizeToGregorian(rawYear);
    }

    private static int NormalizeToGregorian(int rawYear)
    {
        if (rawYear is >= 0 and <= 99)
        {
            // ตาม requirement: ถ้ากรอกสองหลัก (เช่น 68) ให้ตีความเป็นปีไทย 2568
            return (2500 + rawYear) - 543;
        }

        if (rawYear >= 2400)
        {
            // ปี พ.ศ.
            return rawYear - 543;
        }

        if (rawYear >= 1900 && rawYear <= 2399)
        {
            // ปี ค.ศ.
            return rawYear;
        }

        return DateTime.Now.Year;
    }

    private static string NormalizeThaiDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        return input
            .Replace('๐', '0')
            .Replace('๑', '1')
            .Replace('๒', '2')
            .Replace('๓', '3')
            .Replace('๔', '4')
            .Replace('๕', '5')
            .Replace('๖', '6')
            .Replace('๗', '7')
            .Replace('๘', '8')
            .Replace('๙', '9');
    }

    private static (decimal amount, string description) CalculateEntitlement(LevelBudget lev, int mnt)
    {
        if (InRange(mnt, lev.Yrs1, lev.Yre1)) return (lev.Yra1 ?? 0, lev.Yrd1 ?? "");
        if (InRange(mnt, lev.Yrs2, lev.Yre2)) return (lev.Yra2 ?? 0, lev.Yrd2 ?? "");
        if (InRange(mnt, lev.Yrs3, lev.Yre3)) return (lev.Yra3 ?? 0, lev.Yrd3 ?? "");
        if (InRange(mnt, lev.Yrs4, lev.Yre4)) return (lev.Yra4 ?? 0, lev.Yrd4 ?? "");
        return (0, lev.Yrd1 ?? "ยังไม่เข้าเกณฑ์สิทธิ");
    }

    private static bool InRange(int value, int? start, int? end)
        => start.HasValue && end.HasValue && value >= start.Value && value <= end.Value;

    private static MedicalTables ResolveTables(string cmid)
        => cmid?.ToUpperInvariant() switch
        {
            "OA" => new("OA_vemployee", "OA_expopd", "OA_EMPBG", "OA_levbg"),
            "BTS" => new("sup_vemployee", "sup_expopd", "sup_EMPBG", "sup_levbg"),
            _ => new("vemployee", "expopd", "EMPBG", "levbg")
        };

    private sealed record MedicalTables(string EmployeeView, string ExpOpd, string EmpBg, string LevBg);
    private sealed class EmployeeContext { public string CMID { get; set; } = ""; public string EMID { get; set; } = ""; public string DPID { get; set; } = ""; public string FirstName { get; set; } = ""; public string LastName { get; set; } = ""; }
    private sealed class EmployeeMedical { public int Mnt { get; set; } public string IPosition { get; set; } = ""; }
    private sealed class SpecialBudget { public decimal? Yram { get; set; } }
    private sealed class OpdVisit { public DateTime VisitDate { get; set; } public string Hospital { get; set; } = ""; public string Detail { get; set; } = ""; public decimal Amount { get; set; } }
    private sealed class LevelBudget { public string? Yrd1 { get; set; } public int? Yrs1 { get; set; } public int? Yre1 { get; set; } public decimal? Yra1 { get; set; } public string? Yrd2 { get; set; } public int? Yrs2 { get; set; } public int? Yre2 { get; set; } public decimal? Yra2 { get; set; } public string? Yrd3 { get; set; } public int? Yrs3 { get; set; } public int? Yre3 { get; set; } public decimal? Yra3 { get; set; } public string? Yrd4 { get; set; } public int? Yrs4 { get; set; } public int? Yre4 { get; set; } public decimal? Yra4 { get; set; } }
}
