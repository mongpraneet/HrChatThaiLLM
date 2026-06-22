using System.ComponentModel;
using System.Data;
using System.Data.OleDb;
using System.Runtime.Versioning;
using Microsoft.SemanticKernel;
using System.Text.RegularExpressions;

namespace HrChatThaiLLM.Server.Services.Plugins;

[SupportedOSPlatform("windows")]
public class TrainingHistoryPlugins
{
    private readonly string _trainingConn;
    private readonly ILogger<TrainingHistoryPlugins> _logger;

    public TrainingHistoryPlugins(IConfiguration config, ILogger<TrainingHistoryPlugins> logger)
    {
        _logger = logger;
        _trainingConn = config.GetConnectionString("Training")
            ?? throw new InvalidOperationException("Training connection string not found");
    }

    [KernelFunction("get_available_training_classes")]
    [Description("ดูรายชื่อหลักสูตรอบรมที่มีจัดในบริษัทแต่ละปี (ดูหลักสูตรทั้งหมด ไม่ใช่ประวัติของพนักงาน) เช่น ปีนี้มีชื่อหลักสูตรอบรมอะไรบ้าง")]
    public async Task<string> GetAvailableTrainingClasses(
        [Description("คำถามจากผู้ใช้ เช่น ปีนี้มีหลักสูตรอบรมอะไรบ้าง")] string question = "",
        [Description("SubIntent ที่ทำนายได้")] string predictedSubIntent = "")
    {
        try
        {
            var ask = question?.Trim() ?? string.Empty;
            var years = ResolveTrainingYearCandidates(ask);
            
            // ถ้าไม่ระบุปีมา ให้ใช้ปีปัจจุบันเป็นค่าเริ่มต้น
            if (years.Count == 0)
            {
                years = BuildYearCandidates(DateTime.Now.Year);
            }

            var rows = await QueryAvailableClassesAsync(years);

            if (rows.Count == 0)
            {
                var emptyYear = $"ในปี {FormatYearLabel(years)}";
                return $"📚 **ข้อมูลหลักสูตรฝึกอบรม**\nไม่พบข้อมูลการจัดหลักสูตรอบรม {emptyYear}";
            }

            var countOnly = IsCountQuestion(ask, predictedSubIntent);
            var showAll = IsShowAllQuestion(ask);
            var yearText = $"ปี {FormatYearLabel(years)}";
            var totalCount = rows
                .Select(r => $"{r.ClassId}|{r.CRelease}|{r.TrYear}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (countOnly)
            {
                var subjects = rows
                    .Select(r => string.IsNullOrWhiteSpace(r.Subject) ? "(ไม่ระบุชื่อหลักสูตร)" : r.Subject.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .Select((subject, index) => $"{index + 1}. {subject}");

                return $"""
📚 **สรุปจำนวนหลักสูตรฝึกอบรม ({yearText})**
พบหลักสูตรอบรมทั้งหมด `{totalCount}` หลักสูตร

{string.Join("\n", subjects)}
""";
            }

            var lines = rows
                .Take(showAll ? rows.Count : 30)
                .Select((r, index) => $"{index + 1}. {FormatTrainingRow(r)}")
                .ToList();

            var reloadAction = BuildReloadAction(ask, years);
            var moreText = !showAll && rows.Count > 30
                ? $"\n\nแสดง 30 รายการแรกจากทั้งหมด {rows.Count} รายการ\nกดปุ่ม Reload เพื่อแสดงรายการครบทั้งหมด\n{reloadAction}"
                : string.Empty;

            return $"""
📚 **รายชื่อหลักสูตรฝึกอบรม ({yearText})**
พบหลักสูตรอบรมทั้งหมด `{totalCount}` หลักสูตร

{string.Join("\n", lines)}{moreText}
""";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Available classes query failed");
            return "❌ ไม่สามารถดึงข้อมูลหลักสูตรฝึกอบรมได้ในขณะนี้";
        }
    }

    [KernelFunction("get_training_history")]
    [Description("ดูประวัติการฝึกอบรม หลักสูตรที่เคยเข้าอบรม และจำนวนหลักสูตรอบรมของพนักงาน")]
    public async Task<string> GetTrainingHistory(
        [Description("รหัสพนักงาน")] string employeeId,
        [Description("คำถามจากผู้ใช้ เช่น ปีนี้เข้าอบรมอะไรบ้าง ปี 68 เข้าอบรมกี่หลักสูตร")] string question = "",
        [Description("SubIntent ที่ทำนายได้จากไฟล์ training_subintent_training_data.csv")] string predictedSubIntent = "")
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return "ไม่พบรหัสพนักงาน";

        try
        {
            var ask = question?.Trim() ?? string.Empty;
            var years = ResolveTrainingYearCandidates(ask);
            var rows = await QueryTrainingRowsAsync(employeeId.Trim(), years);
            var attendedRows = rows.Where(IsAttended).ToList();
            var displayRows = attendedRows.Count > 0 ? attendedRows : rows;

            if (displayRows.Count == 0)
            {
                var emptyYear = years.Count > 0 ? $"ในปี {FormatYearLabel(years)}" : "ในระบบ";
                return $"📚 **ประวัติการฝึกอบรม**\nยังไม่พบข้อมูลหลักสูตรอบรมของรหัสพนักงาน `{employeeId}` {emptyYear}";
            }

            var countOnly = IsCountQuestion(ask, predictedSubIntent);
            var showAll = IsShowAllQuestion(ask);
            var yearText = years.Count > 0 ? $"ปี {FormatYearLabel(years)}" : "ทั้งหมด";
            var totalCount = displayRows
                .Select(r => $"{r.ClassId}|{r.CRelease}|{r.TrYear}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            if (countOnly)
            {
                var subjects = displayRows
                    .Select(r => string.IsNullOrWhiteSpace(r.Subject) ? "(ไม่ระบุชื่อหลักสูตร)" : r.Subject.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .Select((subject, index) => $"{index + 1}. {subject}");

                return $"""
📚 **สรุปจำนวนหลักสูตรอบรม ({yearText})**
พบข้อมูลเข้าอบรมทั้งหมด `{totalCount}` หลักสูตร

{string.Join("\n", subjects)}
""";
            }

            var lines = displayRows
                .Take(showAll ? displayRows.Count : 30)
                .Select((r, index) => $"{index + 1}. {FormatTrainingRow(r)}")
                .ToList();

            var reloadAction = BuildReloadAction(ask, years);
            var moreText = !showAll && displayRows.Count > 30
                ? $"\n\nแสดง 30 รายการแรกจากทั้งหมด {displayRows.Count} รายการ\nกดปุ่ม Reload เพื่อแสดงรายการครบทั้งหมด\n{reloadAction}"
                : string.Empty;

            return $"""
📚 **ประวัติการฝึกอบรม ({yearText})**
พบข้อมูลเข้าอบรมทั้งหมด `{totalCount}` หลักสูตร

{string.Join("\n", lines)}{moreText}
""";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Training history query failed for {EmployeeId}", employeeId);
            return "❌ ไม่สามารถดึงข้อมูลประวัติการฝึกอบรมได้ในขณะนี้";
        }
    }

    [KernelFunction("get_training_cost")]
    [Description("ดูสรุปงบประมาณหรือค่าใช้จ่ายรวมของการฝึกอบรมของพนักงาน")]
    public async Task<string> GetTrainingCost(
        [Description("รหัสพนักงาน")] string employeeId,
        [Description("คำถามจากผู้ใช้")] string question = "",
        [Description("SubIntent ที่ทำนายได้")] string predictedSubIntent = "")
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return "ไม่พบรหัสพนักงาน";

        try
        {
            var ask = question?.Trim() ?? string.Empty;
            var years = ResolveTrainingYearCandidates(ask);
            var rows = await QueryTrainingRowsAsync(employeeId.Trim(), years);
            var attendedRows = rows.Where(IsAttended).ToList();

            var yearText = years.Count > 0 ? $"ปี {FormatYearLabel(years)}" : "ทั้งหมด";

            if (attendedRows.Count == 0)
            {
                return $"💰 **สรุปค่าใช้จ่ายการอบรม ({yearText})**\nไม่พบข้อมูลการเข้าอบรม หรือไม่มีข้อมูลค่าใช้จ่าย";
            }

            double totalCost = attendedRows.Sum(r => r.Cost ?? 0);
            int courseCount = attendedRows.Count;

            return $"""
💰 **สรุปค่าใช้จ่ายการอบรม ({yearText})**
พบข้อมูลการเข้าอบรมจำนวน `{courseCount}` หลักสูตร
รวมเป็นค่าใช้จ่ายทั้งสิ้น `{totalCost:N2}` บาท
""";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Training cost query failed for {EmployeeId}", employeeId);
            return "❌ ไม่สามารถดึงข้อมูลค่าใช้จ่ายการอบรมได้ในขณะนี้";
        }
    }

    [KernelFunction("get_training_hours")]
    [Description("ดูสรุปชั่วโมงการฝึกอบรมรวมของพนักงาน")]
    public async Task<string> GetTrainingHours(
        [Description("รหัสพนักงาน")] string employeeId,
        [Description("คำถามจากผู้ใช้")] string question = "",
        [Description("SubIntent ที่ทำนายได้")] string predictedSubIntent = "")
    {
        if (string.IsNullOrWhiteSpace(employeeId)) return "ไม่พบรหัสพนักงาน";

        try
        {
            var ask = question?.Trim() ?? string.Empty;
            var years = ResolveTrainingYearCandidates(ask);
            
            var hours = await CalculateTotalTrainingHoursAsync(employeeId.Trim(), years);
            var yearText = years.Count > 0 ? $"ปี {FormatYearLabel(years)}" : "ทั้งหมด";

            return $"""
🕒 **สรุปชั่วโมงการฝึกอบรม ({yearText})**
คุณมีชั่วโมงการเข้าอบรมรวมทั้งสิ้น `{hours:N0}` ชั่วโมง
""";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Training hours query failed for {EmployeeId}", employeeId);
            return "❌ ไม่สามารถดึงข้อมูลชั่วโมงการอบรมได้ในขณะนี้";
        }
    }

    private async Task<double> CalculateTotalTrainingHoursAsync(string employeeId, IReadOnlyCollection<string> years)
    {
        var yearPlaceholders = string.Join(", ", years.Select(_ => "?"));
        var yearFilter = years.Count > 0 ? $"AND tr.TrYear IN ({yearPlaceholders})" : string.Empty;
        var sql = $"""
            SELECT tt.Stime, tt.Etime, tr.TrAccess
            FROM TRTraining tr
            INNER JOIN TRTrainingtime tt
                ON tt.ClassId = tr.ClassId
               AND tt.CRelease = tr.CRelease
               AND tt.TrYear = tr.TrYear
            WHERE tr.EmpNo = ? {yearFilter}
            """;

        using var conn = new OleDbConnection(_trainingConn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.VarWChar, Value = employeeId });

        foreach (var year in years)
        {
            cmd.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.VarWChar, Value = year });
        }

        await conn.OpenAsync();
        using var reader = await cmd.ExecuteReaderAsync();

        double totalHours = 0;
        while (await reader.ReadAsync())
        {
            var access = GetNullableString(reader, "TrAccess");
            if (!string.IsNullOrWhiteSpace(access) && access.Contains("ไม่", StringComparison.OrdinalIgnoreCase))
            {
                continue; // Skip unattended
            }

            var stime = GetNullableDateTime(reader, "Stime");
            var etime = GetNullableDateTime(reader, "Etime");

            if (stime.HasValue && etime.HasValue && etime.Value > stime.Value)
            {
                totalHours += (etime.Value - stime.Value).TotalHours;
            }
        }

        return totalHours;
    }

    private async Task<List<TrainingRow>> QueryTrainingRowsAsync(string employeeId, IReadOnlyCollection<string> years)
    {
        var yearPlaceholders = string.Join(", ", years.Select(_ => "?"));
        var yearFilter = years.Count > 0 ? $"AND tr.TrYear IN ({yearPlaceholders})" : string.Empty;
        var sql = $"""
            SELECT
                c.ClassId,
                c.CRelease,
                c.TrYear,
                c.Subject,
                c.Instructor,
                c.Place,
                c.Cost,
                c.Type,
                MIN(tt.Stime) AS StartTime,
                MAX(tt.Etime) AS EndTime,
                MAX(tr.TrAccess) AS TrAccess
            FROM TRTraining tr
            INNER JOIN TRClass c
                ON c.ClassId = tr.ClassId
               AND c.CRelease = tr.CRelease
               AND c.TrYear = tr.TrYear
            LEFT JOIN TRTrainingtime tt
                ON tt.ClassId = tr.ClassId
               AND tt.CRelease = tr.CRelease
               AND tt.TrYear = tr.TrYear
            WHERE tr.EmpNo = ?
              {yearFilter}
            GROUP BY
                c.ClassId, c.CRelease, c.TrYear, c.Subject, c.Instructor, c.Place, c.Cost, c.Type
            ORDER BY c.TrYear DESC, MAX(tt.Stime) DESC, c.ClassId DESC, c.CRelease DESC;
            """;

        using var conn = new OleDbConnection(_trainingConn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.Parameters.Add(new OleDbParameter { OleDbType = OleDbType.VarWChar, Value = employeeId });

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
                ClassId = GetInt32(reader, "ClassId"),
                CRelease = GetInt32(reader, "CRelease"),
                TrYear = GetString(reader, "TrYear"),
                Subject = GetNullableString(reader, "Subject"),
                Instructor = GetNullableString(reader, "Instructor"),
                Place = GetNullableString(reader, "Place"),
                Cost = GetNullableDouble(reader, "Cost"),
                Type = GetNullableString(reader, "Type"),
                StartTime = GetNullableDateTime(reader, "StartTime"),
                EndTime = GetNullableDateTime(reader, "EndTime"),
                TrAccess = GetNullableString(reader, "TrAccess")
            });
        }

        return rows;
    }

    private async Task<List<TrainingRow>> QueryAvailableClassesAsync(IReadOnlyCollection<string> years)
    {
        var yearPlaceholders = string.Join(", ", years.Select(_ => "?"));
        var yearFilter = years.Count > 0 ? $"WHERE c.TrYear IN ({yearPlaceholders})" : string.Empty;
        var sql = $"""
            SELECT
                c.ClassId,
                c.CRelease,
                c.TrYear,
                c.Subject,
                c.Instructor,
                c.Place,
                c.Cost,
                c.Type,
                MIN(tt.Stime) AS StartTime,
                MAX(tt.Etime) AS EndTime,
                NULL AS TrAccess
            FROM TRClass c
            LEFT JOIN TRTrainingtime tt
                ON tt.ClassId = c.ClassId
               AND tt.CRelease = c.CRelease
               AND tt.TrYear = c.TrYear
            {yearFilter}
            GROUP BY
                c.ClassId, c.CRelease, c.TrYear, c.Subject, c.Instructor, c.Place, c.Cost, c.Type
            ORDER BY c.TrYear DESC, MAX(tt.Stime) DESC, c.ClassId DESC, c.CRelease DESC;
            """;

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
                ClassId = GetInt32(reader, "ClassId"),
                CRelease = GetInt32(reader, "CRelease"),
                TrYear = GetString(reader, "TrYear"),
                Subject = GetNullableString(reader, "Subject"),
                Instructor = GetNullableString(reader, "Instructor"),
                Place = GetNullableString(reader, "Place"),
                Cost = GetNullableDouble(reader, "Cost"),
                Type = GetNullableString(reader, "Type"),
                StartTime = GetNullableDateTime(reader, "StartTime"),
                EndTime = GetNullableDateTime(reader, "EndTime"),
                TrAccess = null
            });
        }

        return rows;
    }

    private static bool IsAttended(TrainingRow row)
    {
        if (string.IsNullOrWhiteSpace(row.TrAccess)) return true;
        var status = row.TrAccess.Trim();
        return !status.Contains("ไม่", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTrainingRow(TrainingRow row)
    {
        var subject = string.IsNullOrWhiteSpace(row.Subject) ? "(ไม่ระบุชื่อหลักสูตร)" : row.Subject.Trim();
        var dateText = FormatDateRange(row.StartTime, row.EndTime);
        var instructor = string.IsNullOrWhiteSpace(row.Instructor) ? "-" : row.Instructor.Trim();
        var place = string.IsNullOrWhiteSpace(row.Place) ? "-" : row.Place.Trim();
        var type = FormatTrainingType(row.Type);
        var status = string.IsNullOrWhiteSpace(row.TrAccess) ? "ไม่ระบุสถานะ" : row.TrAccess.Trim();

        return $"**{subject}** | {dateText} | สถานที่: {place} | ผู้บรรยาย: {instructor} | ประเภท: {type} | สถานะ: {status}";
    }

    private static string FormatDateRange(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue)
        {
            if (start.Value.Date == end.Value.Date) return start.Value.ToString("dd/MM/yyyy");
            return $"{start.Value:dd/MM/yyyy} - {end.Value:dd/MM/yyyy}";
        }

        if (start.HasValue) return start.Value.ToString("dd/MM/yyyy");
        if (end.HasValue) return end.Value.ToString("dd/MM/yyyy");
        return "ไม่ระบุวันที่";
    }

    private static string FormatTrainingType(string? type)
        => type?.Trim() switch
        {
            "ภายนอก" => "ภายนอก",
            "ภายใน" => "ภายใน",
            "" or null => "ภายใน",
            var value => value
        };

    private static bool IsCountQuestion(string question, string predictedSubIntent)
    {
        if (predictedSubIntent.Equals("TrainingCount", StringComparison.OrdinalIgnoreCase)) return true;

        return question.Contains("กี่", StringComparison.OrdinalIgnoreCase)
            || question.Contains("จำนวน", StringComparison.OrdinalIgnoreCase)
            || question.Contains("รวม", StringComparison.OrdinalIgnoreCase)
            || question.Contains("count", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShowAllQuestion(string question)
    {
        return question.Contains("แสดงทั้งหมด", StringComparison.OrdinalIgnoreCase)
            || question.Contains("แสดงครบ", StringComparison.OrdinalIgnoreCase)
            || question.Contains("ดูทั้งหมด", StringComparison.OrdinalIgnoreCase)
            || question.Contains("โหลดทั้งหมด", StringComparison.OrdinalIgnoreCase)
            || question.Contains("ครบทั้งหมด", StringComparison.OrdinalIgnoreCase)
            || question.Contains("all", StringComparison.OrdinalIgnoreCase)
            || question.Contains("full", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildReloadAction(string question, IReadOnlyCollection<string> years)
    {
        var yearText = years.Count > 0 ? $" {FormatYearLabel(years)}" : string.Empty;
        var cleanQuestion = question
            .Replace("[", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (string.IsNullOrWhiteSpace(cleanQuestion))
        {
            cleanQuestion = "ประวัติการฝึกอบรม";
        }

        return $"[ADMIN_RELOAD:แสดงทั้งหมด {cleanQuestion}{yearText}]";
    }

    private static List<string> ResolveTrainingYearCandidates(string question)
    {
        var normalized = NormalizeThaiDigits(question);
        var currentYear = DateTime.Now.Year;

        var match = Regex.Match(normalized, @"(?<!\d)(\d{2,4})(?!\d)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var rawYear))
        {
            return BuildYearCandidates(rawYear);
        }

        if (normalized.Contains("ปีนี้", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("this year", StringComparison.OrdinalIgnoreCase))
        {
            return BuildYearCandidates(currentYear);
        }

        if (normalized.Contains("ปีที่แล้ว", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ปีก่อน", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("last year", StringComparison.OrdinalIgnoreCase))
        {
            return BuildYearCandidates(currentYear - 1);
        }

        return [];
    }

    private static List<string> BuildYearCandidates(int rawYear)
    {
        var gregorianYear = DateNormalizer.NormalizeYear(rawYear);
        var buddhistYear = gregorianYear + 543;

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

    private static string FormatYearLabel(IReadOnlyCollection<string> years)
    {
        var buddhist = years.FirstOrDefault(y => int.TryParse(y, out var n) && n >= 2400);
        if (!string.IsNullOrWhiteSpace(buddhist)) return buddhist;

        return string.Join("/", years);
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

    private static int GetInt32(IDataRecord reader, string name)
    {
        var value = reader[name];
        if (value == DBNull.Value) return 0;
        return Convert.ToInt32(value);
    }

    private static string GetString(IDataRecord reader, string name)
    {
        var value = reader[name];
        return value == DBNull.Value ? string.Empty : Convert.ToString(value)?.Trim() ?? string.Empty;
    }

    private static string? GetNullableString(IDataRecord reader, string name)
    {
        var value = reader[name];
        return value == DBNull.Value ? null : Convert.ToString(value)?.Trim();
    }

    private static double? GetNullableDouble(IDataRecord reader, string name)
    {
        var value = reader[name];
        return value == DBNull.Value ? null : Convert.ToDouble(value);
    }

    private static DateTime? GetNullableDateTime(IDataRecord reader, string name)
    {
        var value = reader[name];
        return value == DBNull.Value ? null : Convert.ToDateTime(value);
    }

    private sealed class TrainingRow
    {
        public int ClassId { get; set; }
        public int CRelease { get; set; }
        public string TrYear { get; set; } = "";
        public string? Subject { get; set; }
        public string? Instructor { get; set; }
        public string? Place { get; set; }
        public double? Cost { get; set; }
        public string? Type { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string? TrAccess { get; set; }
    }
}
