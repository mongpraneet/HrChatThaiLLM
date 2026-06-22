namespace HrChatThaiLLM.Server.Models;

public class EmployeeInfo
{
    public string EmployeeId   { get; set; } = "";
    public string FirstName    { get; set; } = "";
    public string LastName     { get; set; } = "";
    public string FullName     => $"{FirstName} {LastName}".Trim();
    public string DeptId       { get; set; } = "";
    public string DeptName     { get; set; } = "";
    public string DeviId       { get; set; } = "";
    public string DeviName     { get; set; } = "";
    public string LevelId      { get; set; } = "";
    public string LevelName    { get; set; } = "";
    public string PositionId   { get; set; } = "";
    public string PositionName { get; set; } = "";
    public string CompanyId    { get; set; } = "";
    public string CompanyName  { get; set; } = "";
    public string Gender { get; set; } = "";
}

public class ChatSessionInfo
{
    public Guid     SessionId    { get; set; }
    public string   EmployeeId   { get; set; } = "";
    public string   SessionTitle { get; set; } = "";
    public DateTime CreatedAt    { get; set; }
    public DateTime UpdatedAt    { get; set; }
    public bool     IsActive     { get; set; }
}

public class ChatMessageRecord
{
    public long     MessageId  { get; set; }
    public Guid     SessionId  { get; set; }
    public string   EmployeeId { get; set; } = "";
    public string   Role       { get; set; } = "";
    public string   Content    { get; set; } = "";
    public DateTime CreatedAt  { get; set; }
    public int?     TokensUsed { get; set; }
}
