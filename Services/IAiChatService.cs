namespace HrChatThaiLLM.Server.Services;

public interface IAiChatService
{
    Task<string> ProcessMessageAsync(string userId, string userMessage);
    IAsyncEnumerable<string> ProcessMessageStreamingAsync(string userId, string userMessage);
    Task<object?> FetchDataAsync(string userId, string dataType, Dictionary<string, string> parameters);
    Task<string> ExecuteClaimStatusAsync(string userId, string question = "สถานะเคลมค่ารักษา");
    string BuildAssistantIdentityFallback();
    Task InitializeAsync();
    void SaveChartSummary(string userId, string chartType, int? buddhistYear);
}
