using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using HrChatThaiLLM.Server.Models;
using HrChatThaiLLM.Server.Services;

namespace HrChatThaiLLM.Server.Pages;

public class ChatModel : PageModel
{
    private readonly IChatHistoryService _chatHistory;
    private readonly ILogger<ChatModel> _logger;

    public ChatModel(IChatHistoryService chatHistory, ILogger<ChatModel> logger)
    {
        _chatHistory = chatHistory;
        _logger = logger;
    }

    public string EmployeeId   { get; private set; } = "";
    public string EmployeeName { get; private set; } = "";
    public string DeptName     { get; private set; } = "";
    public string PositionName { get; private set; } = "";

    public List<ChatSessionInfo> RecentSessions { get; private set; } = new();
    public Guid CurrentSessionId { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? sessionId = null)
    {
        // ตรวจสอบ Session
        EmployeeId = HttpContext.Session.GetString("EmployeeId") ?? "";
        if (string.IsNullOrEmpty(EmployeeId))
            return RedirectToPage("/Login");

        EmployeeName = HttpContext.Session.GetString("EmployeeName") ?? "";
        DeptName = HttpContext.Session.GetString("DeptName") ?? "";
        PositionName = HttpContext.Session.GetString("PositionName") ?? "";

        // โหลดรายการแชทล่าสุด
        RecentSessions = await _chatHistory.GetSessionsAsync(EmployeeId);

        // ── สร้าง Session ใหม่เฉพาะกรณีที่จำเป็นจริงๆ ─────────────────────
        if (sessionId != null && Guid.TryParse(sessionId, out var sid))
        {
            CurrentSessionId = sid;
        }
        else if (RecentSessions.Any())
        {
            CurrentSessionId = RecentSessions.First().SessionId;
        }
        else
        {
            // สร้างใหม่เฉพาะตอนที่ยังไม่มี Session เลย
            CurrentSessionId = await _chatHistory.CreateSessionAsync(EmployeeId);
        }

        return Page();
    }

    public IActionResult OnPostLogout()
    {
        HttpContext.Session.Clear();
        return RedirectToPage("/Login");
    }
}
