namespace HrChatThaiLLM.Server.Models
{
    public class PluginRow
    {
        public int Id { get; set; }
        public string PluginKey { get; set; } = "";
        public string? Description { get; set; }
    }

    public class ResponseTemplateRow
    {
        public int Id { get; set; }
        public int? PluginId { get; set; }
        public string Section { get; set; } = "";
        public string TemplateText { get; set; } = "";
        public bool IsActive { get; set; }
    }
}
