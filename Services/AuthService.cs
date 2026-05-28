using HrChatThaiLLM.Server.Models;

namespace HrChatThaiLLM.Server.Services;

public interface IAuthService
{
    Task<EmployeeInfo?> AuthenticateAsync(string employeeId, string password);
    Task<EmployeeInfo?> GetEmployeeInfoAsync(string employeeId);
}

public class AuthService : IAuthService
{
    private readonly ISqlExecutorService _sql;
    private readonly ILogger<AuthService> _logger;

    public AuthService(ISqlExecutorService sql, ILogger<AuthService> logger)
    {
        _sql = sql;
        _logger = logger;
    }

    /// <summary>
    /// ตรวจสอบการ Login จากตาราง EMPDA 
    /// user = EMID, password = EMPS (รองรับพาสเวิร์ดว่างหรือ NULL)
    /// </summary>
    public async Task<EmployeeInfo?> AuthenticateAsync(string employeeId, string password)
    {
        try
        {
            // ห้ามแสดง IDNO ในผลลัพธ์ - ดึงเฉพาะข้อมูลที่จำเป็นและถูกต้องตาม Data Dictionary
            var sql = @"
                SELECT 
                    e.EMID,
                    e.EMFNT,
                    e.EMLNT,
                    e.DPID,
                    d.DPDST AS DeptName,
                    e.DVID,
                    v.DVDST AS DeviName,
                    e.LVID,
                    l.LVDST AS LevelName,
                    e.PSID,
                    p.PSDST AS PositionName,
                    e.CMID,
                    c.CMDST AS CompanyName,
                    e.EMSX
                FROM EMPDA e
                LEFT JOIN EmDept  d ON e.DPID = d.DPID AND e.CMID = d.CMID
                LEFT JOIN EMDevi  v ON e.DVID = v.DVID AND e.CMID = v.CMID
                LEFT JOIN EMLevel l ON e.LVID = l.LVID AND e.CMID = l.CMID
                LEFT JOIN EMPosi  p ON e.PSID = p.PSID AND e.CMID = p.CMID
                LEFT JOIN EMCOMP  c ON e.CMID = c.CMID
                WHERE e.EMID = @EmployeeId
                  AND ISNULL(e.EMPS, '') = @Password
            ";

            var result = await _sql.QueryFirstOrDefaultAsync<dynamic>(sql, new
            {
                EmployeeId = employeeId,
                Password = password
            });

            if (result == null) return null;

            return new EmployeeInfo
            {
                EmployeeId = result.EMID,
                FirstName = result.EMFNT ?? "",
                LastName = result.EMLNT ?? "",
                DeptId = result.DPID ?? "",
                DeptName = result.DeptName ?? "",
                DeviId = result.DVID ?? "",
                DeviName = result.DeviName ?? "",
                LevelId = result.LVID ?? "",
                LevelName = result.LevelName ?? "",
                PositionId = result.PSID ?? "",
                Gender = result.EMSX ?? "",
                PositionName = result.PositionName ?? "",
                CompanyId = result.CMID ?? "",
                CompanyName = result.CompanyName ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication error for employee {EmployeeId}", employeeId);
            return null;
        }
    }

    /// <summary>
    /// ดึงรายละเอียดข้อมูลพนักงานตามรหัสโดยไม่ต้องตรวจสอบรหัสผ่าน
    /// </summary>
    public async Task<EmployeeInfo?> GetEmployeeInfoAsync(string employeeId)
    {
        try
        {
            var sql = @"
                SELECT 
                    e.EMID,
                    e.EMFNT,
                    e.EMLNT,
                    e.DPID,
                    d.DPDST AS DeptName,
                    e.DVID,
                    v.DVDST AS DeviName,
                    e.LVID,
                    l.LVDST AS LevelName,
                    e.PSID,
                    p.PSDST AS PositionName,
                    e.CMID,
                    c.CMDST AS CompanyName
                FROM EMPDA e
                LEFT JOIN EmDept  d ON e.DPID = d.DPID AND e.CMID = d.CMID
                LEFT JOIN EMDevi  v ON e.DVID = v.DVID AND e.CMID = v.CMID
                LEFT JOIN EMLevel l ON e.LVID = l.LVID AND e.CMID = l.CMID
                LEFT JOIN EMPosi  p ON e.PSID = p.PSID AND e.CMID = p.CMID
                LEFT JOIN EMCOMP  c ON e.CMID = c.CMID
                WHERE e.EMID = @EmployeeId
            ";

            var result = await _sql.QueryFirstOrDefaultAsync<dynamic>(sql, new { EmployeeId = employeeId });
            if (result == null) return null;

            return new EmployeeInfo
            {
                EmployeeId = result.EMID,
                FirstName = result.EMFNT ?? "",
                LastName = result.EMLNT ?? "",
                DeptId = result.DPID ?? "",
                DeptName = result.DeptName ?? "",
                DeviId = result.DVID ?? "",
                DeviName = result.DeviName ?? "",
                LevelId = result.LVID ?? "",
                LevelName = result.LevelName ?? "",
                PositionId = result.PSID ?? "",
                PositionName = result.PositionName ?? "",
                CompanyId = result.CMID ?? "",
                CompanyName = result.CompanyName ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetEmployeeInfo error for {EmployeeId}", employeeId);
            return null;
        }
    }
}