using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace HrChatThaiLLM.Server.Services.Plugins;

public class LeavePlugin
{
    private readonly ISqlExecutorService _sql;

    public LeavePlugin(ISqlExecutorService sql) => _sql = sql;

    [KernelFunction("get_leave_balance")]
    [Description("ดึงยอดวันลาคงเหลือของพนักงาน")]
    public async Task<string> GetLeaveBalance(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sqlQuery = @"
            SELECT lb.AnnualLeave, lb.SickLeave, lb.PersonalLeave,
                   e.FirstName + ' ' + e.LastName as EmployeeName
            FROM LeaveBalances lb
            JOIN Employees e ON lb.EmployeeId = e.EmployeeId
            WHERE lb.EmployeeId = @EmployeeId AND lb.Year = YEAR(GETDATE())
        ";

        var result = await _sql.QueryFirstOrDefaultAsync<dynamic>(sqlQuery, new { EmployeeId = employeeId });

        if (result == null)
            return $"ไม่พบข้อมูลวันลาของพนักงาน {employeeId}";

        return $"พนักงาน: {result.EmployeeName}\n• ลาประจำปี: {result.AnnualLeave} วัน\n• ลาป่วย: {result.SickLeave} วัน\n• ลากิจ: {result.PersonalLeave} วัน";
    }

    [KernelFunction("get_leave_requests")]
    [Description("ดูประวัติการลางาน")]
    public async Task<string> GetLeaveRequestStatus(
        [Description("รหัสพนักงาน")] string employeeId)
    {
        var sqlQuery = @"
            SELECT TOP 3 RequestId, LeaveType, StartDate, EndDate, Status
            FROM LeaveRequests
            WHERE EmployeeId = @EmployeeId
            ORDER BY CreatedAt DESC
        ";

        var results = await _sql.QueryAsync<dynamic>(sqlQuery, new { EmployeeId = employeeId });

        if (!results.Any())
            return "ไม่พบประวัติการลางาน";

        var lines = results.Select(r => $"• #{r.RequestId} {r.LeaveType} [{r.Status}]");
        return $"ประวัติการลา:\n{string.Join("\n", lines)}";
    }
}