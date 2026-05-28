namespace HrChatThaiLLM.Server.Models
{
    using System;

    public class ChatSummaryItem
    {
        public string Intent { get; set; } = "";
        public string Topic { get; set; } = "";
        public string KeyInfo { get; set; } = "";

        // นับจำนวนครั้งที่ถามซ้ำ (เริ่มที่ 1)
        public int AskCount { get; set; } = 1;

        public DateTime Timestamp { get; set; }
    }
}
