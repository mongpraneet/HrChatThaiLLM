using Microsoft.AspNetCore.Mvc;
using HrChatThaiLLM.Server.Services;

namespace HrChatThaiLLM.Server.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly IAiChatService      _aiService;
    private readonly IChatHistoryService _chatHistory;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        IAiChatService aiService,
        IChatHistoryService chatHistory,
        ILogger<ChatController> logger)
    {
        _aiService    = aiService;
        _chatHistory  = chatHistory;
        _logger       = logger;
    }

    // ─── REST Message ──────────────────────────────────────────────────────────
    [HttpPost("message")]
    public async Task<IActionResult> PostMessage([FromBody] ChatRequest request)
    {
        try
        {
            var response = await _aiService.ProcessMessageAsync(request.UserId, request.Message);
            return Ok(new ChatResponse { Content = response, Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PostMessage");
            return StatusCode(500, new ChatResponse { Content = $"Error: {ex.Message}", Success = false });
        }
    }

    // ─── Sessions ──────────────────────────────────────────────────────────────
    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest request)
    {
        try
        {
            var sessionId = await _chatHistory.CreateSessionAsync(request.EmployeeId);
            return Ok(new { sessionId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateSession error");
            return StatusCode(500);
        }
    }

    [HttpGet("sessions/{employeeId}")]
    public async Task<IActionResult> GetSessions(string employeeId)
    {
        // ตรวจสอบว่าเป็นของตัวเองเท่านั้น
        var sessionEmpId = HttpContext.Session.GetString("EmployeeId");
        if (sessionEmpId != employeeId) return Forbid();

        var sessions = await _chatHistory.GetSessionsAsync(employeeId);
        return Ok(sessions);
    }

    [HttpGet("sessions/{sessionId:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid sessionId)
    {
        var employeeId = HttpContext.Session.GetString("EmployeeId");
        if (string.IsNullOrEmpty(employeeId)) return Unauthorized();

        var messages = await _chatHistory.GetMessagesAsync(sessionId, employeeId);
        return Ok(messages);
    }

    [HttpPost("sessions/{sessionId:guid}/messages")]
    public async Task<IActionResult> SaveMessage(Guid sessionId, [FromBody] SaveMessageRequest request)
    {
        var employeeId = HttpContext.Session.GetString("EmployeeId");
        if (string.IsNullOrEmpty(employeeId)) return Unauthorized();

        await _chatHistory.SaveMessageAsync(sessionId, request.EmployeeId, request.Role, request.Content);
        return Ok();
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(Guid sessionId)
    {
        var employeeId = HttpContext.Session.GetString("EmployeeId");
        if (string.IsNullOrEmpty(employeeId)) return Unauthorized();

        await _chatHistory.DeleteSessionAsync(sessionId, employeeId);
        return Ok();
    }
}

// ── Request/Response Models ──────────────────────────────────────────────────
public class ChatRequest
{
    public string UserId  { get; set; } = "";
    public string Message { get; set; } = "";
}

public class ChatResponse
{
    public string Content { get; set; } = "";
    public bool   Success { get; set; }
}

public class CreateSessionRequest
{
    public string EmployeeId { get; set; } = "";
}

public class SaveMessageRequest
{
    public string EmployeeId { get; set; } = "";
    public string Role       { get; set; } = "";
    public string Content    { get; set; } = "";
}
