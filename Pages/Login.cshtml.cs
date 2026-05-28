using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using HrChatThaiLLM.Server.Services;

namespace HrChatThaiLLM.Server.Pages;

public class LoginModel : PageModel
{
    private readonly IAuthService _authService;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IAuthService authService, ILogger<LoginModel> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [BindProperty] public string EmployeeId { get; set; } = "";
    [BindProperty] public string Password   { get; set; } = "";
    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        // ถ้า login แล้วให้ redirect ไป chat
        if (HttpContext.Session.GetString("EmployeeId") != null)
            return RedirectToPage("/Chat");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(EmployeeId))
        {
            ErrorMessage = "กรุณากรอกรหัสพนักงาน";
            return Page();
        }

        // ตรวจสอบเผื่อกรณีค่าที่ Bind มาเป็น null ให้แปลงเป็น string ว่าง ""
        var loginPassword = Password ?? "";

        var employee = await _authService.AuthenticateAsync(EmployeeId.Trim(), loginPassword);

        if (employee == null)
        {
            ErrorMessage = "รหัสพนักงานหรือรหัสผ่านไม่ถูกต้อง กรุณาลองใหม่อีกครั้ง";
            _logger.LogWarning("Failed login attempt for employee {EmployeeId}", EmployeeId);
            return Page();
        }

        // บันทึก Session
        HttpContext.Session.SetString("EmployeeId",   employee.EmployeeId);
        HttpContext.Session.SetString("EmployeeName", employee.FullName);
        HttpContext.Session.SetString("DeptId",       employee.DeptId);
        HttpContext.Session.SetString("DeptName",     employee.DeptName);
        HttpContext.Session.SetString("PositionName", employee.PositionName);

        _logger.LogInformation("Employee {EmployeeId} logged in successfully", employee.EmployeeId);
        return RedirectToPage("/Chat");
    }
}
