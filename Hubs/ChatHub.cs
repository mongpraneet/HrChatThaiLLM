using Microsoft.AspNetCore.SignalR;
using HrChatThaiLLM.Server.Models;
using HrChatThaiLLM.Server.Services;

namespace HrChatThaiLLM.Server.Hubs;

public class ChatHub : Hub
{
    private readonly IAiChatService _aiService;
    private readonly IPromptChoiceRouter _promptChoiceRouter;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IAiChatService aiService,
        IPromptChoiceRouter promptChoiceRouter,
        ILogger<ChatHub> logger)
    {
        _aiService = aiService;
        _promptChoiceRouter = promptChoiceRouter;
        _logger = logger;
    }

    // ส่งข้อความแบบปกติ (รอคำตอบครบ)
    public async Task SendMessage(string userId, string message)
    {
        try
        {
            await Clients.Caller.SendAsync("ReceiveStatus", "typing");

            if (_promptChoiceRouter.TryHandle(userId, message, out var choiceResponse))
            {
                if (choiceResponse != null && choiceResponse.StartsWith("[ACTION:RUN_CLAIM_STATUS"))
                {
                    string question = "สถานะเคลมค่ารักษา";
                    var parts = choiceResponse.Split(' ', 2);
                    if (parts.Length > 1)
                    {
                        string adYear = parts[1].TrimEnd(']');
                        question = $"สถานะเคลมค่ารักษา ปี {adYear}";
                    }
                    var claim = await _aiService.ExecuteClaimStatusAsync(userId, question);
                    await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage
                    {
                        Role = "assistant",
                        Content = claim,
                        Timestamp = DateTime.Now
                    });
                    await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                    return;
                }
                if (choiceResponse == "[ACTION:SHOW_PROFILE]")
                {
                    var profile = await _aiService.ProcessMessageAsync(userId, "ข้อมูลส่วนตัวของฉัน");
                    await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage
                    {
                        Role = "assistant",
                        Content = profile,
                        Timestamp = DateTime.Now
                    });
                    await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                    return;
                }

                if (choiceResponse == "[ACTION:FALLBACK]")
                {
                    var fb = _aiService.BuildAssistantIdentityFallback();
                    await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage
                    {
                        Role = "assistant",
                        Content = fb,
                        Timestamp = DateTime.Now
                    });
                    await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                    return;
                }

                await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage
                {
                    Role = "assistant",
                    Content = choiceResponse,
                    Timestamp = DateTime.Now
                });
                await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                return;
            }

            var response = await _aiService.ProcessMessageAsync(userId, message);

            await Clients.Caller.SendAsync("ReceiveMessage", new ChatMessage
            {
                Role = "assistant",
                Content = response,
                Timestamp = DateTime.Now
            });

            await Clients.Caller.SendAsync("ReceiveStatus", "idle");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendMessage");
            await Clients.Caller.SendAsync("ReceiveError", "เกิดข้อผิดพลาด กรุณาลองใหม่");
        }
    }

    // ส่งข้อความแบบ Streaming (แสดงคำตอบทีละคำ)
    public async Task SendMessageStreaming(string userId, string message)
    {
        try
        {
            await Clients.Caller.SendAsync("ReceiveStatus", "typing");

            if (_promptChoiceRouter.TryHandle(userId, message, out var choiceResponse))
            {
                if (choiceResponse != null && choiceResponse.StartsWith("[ACTION:RUN_CLAIM_STATUS"))
                {
                    string question = "สถานะเคลมค่ารักษา";
                    var parts = choiceResponse.Split(' ', 2);
                    if (parts.Length > 1)
                    {
                        string adYear = parts[1].TrimEnd(']');
                        question = $"สถานะเคลมค่ารักษา ปี {adYear}";
                    }
                    var claim = await _aiService.ExecuteClaimStatusAsync(userId, question);
                    await Clients.Caller.SendAsync("ReceiveChunk", claim);
                    await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                    await Clients.Caller.SendAsync("ReceiveComplete", true);
                    return;
                }

                if (choiceResponse == "[ACTION:FALLBACK]")
                {
                    var fb = _aiService.BuildAssistantIdentityFallback();
                    await Clients.Caller.SendAsync("ReceiveChunk", fb);
                    await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                    await Clients.Caller.SendAsync("ReceiveComplete", true);
                    return;
                }

                if (choiceResponse == "[ACTION:SHOW_PROFILE]")
                {
                    var profile = await _aiService.ProcessMessageAsync(userId, "ข้อมูลส่วนตัวของฉัน");
                    await Clients.Caller.SendAsync("ReceiveChunk", profile);
                    await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                    await Clients.Caller.SendAsync("ReceiveComplete", true);
                    return;
                }

                await Clients.Caller.SendAsync("ReceiveChunk", choiceResponse);
                await Clients.Caller.SendAsync("ReceiveStatus", "idle");
                await Clients.Caller.SendAsync("ReceiveComplete", true);
                return;
            }

            await foreach (var chunk in _aiService.ProcessMessageStreamingAsync(userId, message))
            {
                await Clients.Caller.SendAsync("ReceiveChunk", chunk);
            }

            await Clients.Caller.SendAsync("ReceiveStatus", "idle");
            await Clients.Caller.SendAsync("ReceiveComplete", true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendMessageStreaming");
            await Clients.Caller.SendAsync("ReceiveError", "เกิดข้อผิดพลาดในการสตรีม");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // แจ้ง Server ว่า Client render กราฟเสร็จแล้ว → บันทึกลง Summary Cache
    // ══════════════════════════════════════════════════════════════════════════
    /// <param name="userId">รหัสพนักงาน</param>
    /// <param name="chartType">ประเภทกราฟ: "attendance" | "medical" | "leave"</param>
    /// <param name="buddhistYear">ปี พ.ศ. ที่ดู (null = ปีปัจจุบัน)</param>
    public async Task NotifyChartViewed(string userId, string chartType, int? buddhistYear = null)
    {
        try
        {
            _aiService.SaveChartSummary(userId, chartType, buddhistYear);
            _logger.LogInformation(
                "Chart viewed — userId={UserId} type={ChartType} year={Year}",
                userId, chartType, buddhistYear);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in NotifyChartViewed");
        }
        await Task.CompletedTask;
    }
}