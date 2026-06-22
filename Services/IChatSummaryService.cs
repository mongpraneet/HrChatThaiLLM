namespace HrChatThaiLLM.Server.Services
{
    using HrChatThaiLLM.Server.Models;
    using System.Collections.Generic;

    public interface IChatSummaryService
    {
        // ใช้ชื่อ AddOrUpdate เพื่อความชัดเจนว่ามีการตรวจสอบของเก่า
        void AddOrUpdateSummaryItem(string sessionId, ChatSummaryItem item);

        List<ChatSummaryItem> GetSummaryItems(string sessionId);
        void ClearSummary(string sessionId);
    }
}
