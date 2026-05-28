using System.Collections.Concurrent;
using HrChatThaiLLM.Server.Models;
using Microsoft.Extensions.Caching.Memory;

namespace HrChatThaiLLM.Server.Services;

public class ChatSummaryService : IChatSummaryService
{
    private readonly IMemoryCache _cache;
    private const string KeyPrefix = "ChatSummary_";
    
    // 🔒 ใช้ ConcurrentDictionary เพื่อเก็บ Lock object สำหรับแต่ละ SessionId
    // ป้องกัน Race Condition เมื่อมี Request เข้ามาพร้อมกัน
    private static readonly ConcurrentDictionary<string, object> _sessionLocks = new();

    public ChatSummaryService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public void AddOrUpdateSummaryItem(string sessionId, ChatSummaryItem item)
    {
        // 🔒 สร้างหรือดึง Lock object สำหรับ Session นี้
        var lockObj = _sessionLocks.GetOrAdd(sessionId, _ => new object());

        lock (lockObj)
        {
            var items = GetSummaryItems(sessionId);
            
            // 🔍 ค้นหาว่ามี Intent เดิมอยู่แล้วหรือไม่
            var existingItem = items.FirstOrDefault(i => i.Intent == item.Intent);
            
            if (existingItem != null)
            {
                // ✅ อัปเดตข้อมูลล่าสุด (ไม่เพิ่มรายการใหม่)
                existingItem.KeyInfo = TruncateKeyInfo(item.KeyInfo); // ตัดข้อความยาว
                existingItem.AskCount++;  // ✅ นับจำนวนครั้งที่ถามซ้ำ
                existingItem.Timestamp = DateTime.Now;
            }
            else
            {
                // ✅ เพิ่มรายการใหม่
                items.Add(item);
            }
            
            _cache.Set(KeyPrefix + sessionId, items, TimeSpan.FromMinutes(60));
        }
    }

    public List<ChatSummaryItem> GetSummaryItems(string sessionId)
    {
        return _cache.GetOrCreate(KeyPrefix + sessionId, _ => new List<ChatSummaryItem>())!;
    }

    public void ClearSummary(string sessionId)
    {
        _cache.Remove(KeyPrefix + sessionId);
        _sessionLocks.TryRemove(sessionId, out _); // ลบ lock object เมื่อ clear summary
    }

    // ✂️ ฟังก์ชันตัดข้อความยาวเกินไป (Max 500 ตัวอักษร)
    private string TruncateKeyInfo(string info, int maxLength = 500)
    {
        if (string.IsNullOrEmpty(info)) return "";
        
        if (info.Length <= maxLength) return info;
        
        // ตัดและเพิ่ม "..." เพื่อบอกว่ามีการตัดข้อความ
        return info.Substring(0, maxLength - 3) + "...";
    }
}
