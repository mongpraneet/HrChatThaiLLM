namespace HrChatThaiLLM.Server.Services
{
    using HrChatThaiLLM.Server.Models;
    using Microsoft.Extensions.Caching.Memory;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public class ChatSummaryService : IChatSummaryService
    {
        private readonly IMemoryCache _cache;
        private const string KeyPrefix = "ChatSummary_";

        public ChatSummaryService(IMemoryCache cache)
        {
            _cache = cache;
        }

        public void AddOrUpdateSummaryItem(string sessionId, ChatSummaryItem item)
        {
            var items = GetSummaryItems(sessionId);

            // 🔍 ค้นหาว่ามี Intent เดิมอยู่แล้วหรือไม่ (เพื่อไม่ให้ข้อมูลซ้ำ)
            var existingItem = items.FirstOrDefault(i => i.Intent == item.Intent);

            if (existingItem != null)
            {
                // ✅ อัปเดตข้อมูลล่าสุดและเพิ่มจำนวนครั้งที่ถาม
                existingItem.KeyInfo = item.KeyInfo;
                existingItem.AskCount++;
                existingItem.Timestamp = DateTime.Now;
            }
            else
            {
                // ✅ เพิ่มรายการใหม่ถ้ายังไม่เคยถามเรื่องนี้มี
                items.Add(item);
            }

            // เก็บลง Cache ตั้งเวลาหมดอายุ 60 นาที (ปรับได้ตามต้องการ)
            _cache.Set(KeyPrefix + sessionId, items, TimeSpan.FromMinutes(60));
        }

        public List<ChatSummaryItem> GetSummaryItems(string sessionId)
        {
            return _cache.GetOrCreate(KeyPrefix + sessionId, _ => new List<ChatSummaryItem>())!;
        }

        public void ClearSummary(string sessionId)
        {
            _cache.Remove(KeyPrefix + sessionId);
        }
    }
}
