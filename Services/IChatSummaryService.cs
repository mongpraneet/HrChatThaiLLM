using HrChatThaiLLM.Server.Models;

namespace HrChatThaiLLM.Server.Services;

public interface IChatSummaryService
{
    void AddOrUpdateSummaryItem(string sessionId, ChatSummaryItem item);
    List<ChatSummaryItem> GetSummaryItems(string sessionId);
    void ClearSummary(string sessionId);
}
