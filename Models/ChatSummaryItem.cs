namespace HrChatThaiLLM.Server.Models;

public class ChatSummaryItem
{
    public string Intent { get; set; } = "";
    public string Topic { get; set; } = "";
    public string KeyInfo { get; set; } = "";
    public int AskCount { get; set; } = 1;
    public DateTime Timestamp { get; set; }
}
