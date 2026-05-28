using System.Text.Json.Serialization;

namespace HrChatThaiLLM.Server.Models;

public class ThaiLLMRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "typhoon-s-thaillm-8b-instruct";

    [JsonPropertyName("messages")]
    public List<Message> Messages { get; set; } = new();

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 2048;

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.3f;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;
}

public class Message
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public class ThaiLLMResponse
{
    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; } = new();
}

public class Choice
{
    [JsonPropertyName("message")]
    public Message? Message { get; set; }

    [JsonPropertyName("delta")]
    public Message? Delta { get; set; }
}