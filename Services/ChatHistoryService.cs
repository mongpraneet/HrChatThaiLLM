using Dapper;
using Microsoft.Data.SqlClient;
using HrChatThaiLLM.Server.Models;

namespace HrChatThaiLLM.Server.Services;

public interface IChatHistoryService
{
    Task<Guid> CreateSessionAsync(string employeeId);
    Task SaveMessageAsync(Guid sessionId, string employeeId, string role, string content);
    Task<List<ChatSessionInfo>> GetSessionsAsync(string employeeId, int top = 20);
    Task<List<ChatMessageRecord>> GetMessagesAsync(Guid sessionId, string employeeId);
    Task<List<ChatMessageRecord>> GetAllMessagesByDateAsync(DateTime date, string? employeeId = null, int skip = 0, int take = 200);
    Task DeleteSessionAsync(Guid sessionId, string employeeId);
    Task SaveAuditLogAsync(string employeeId, string userMessage, string? matchedPlugin, int executionTimeMs);
}

public class ChatHistoryService : IChatHistoryService
{
    private readonly string _connectionString;
    private readonly ILogger<ChatHistoryService> _logger;
    private static readonly object _lock = new();
    private static bool _tablesInitialized = false;

    public ChatHistoryService(IConfiguration config, ILogger<ChatHistoryService> logger)
    {
        _connectionString = config.GetConnectionString("ChatHistoryDatabase")
            ?? throw new InvalidOperationException("ChatHistoryDatabase connection string not found");
        _logger = logger;

        EnsureTablesCreated();
    }

    public async Task SaveAuditLogAsync(string employeeId, string userMessage, string? matchedPlugin, int executionTimeMs)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                INSERT INTO AuditLogs (EmployeeId, UserMessage, MatchedPlugin, ExecutionTimeMs)
                VALUES (@EmployeeId, @UserMessage, @MatchedPlugin, @ExecutionTimeMs);
            ";
            await conn.ExecuteAsync(sql, new
            {
                EmployeeId = employeeId,
                UserMessage = userMessage,
                MatchedPlugin = matchedPlugin,
                ExecutionTimeMs = executionTimeMs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save audit log for {EmployeeId}", employeeId);
        }
    }
    private void EnsureTablesCreated()
    {
        if (_tablesInitialized) return;

        lock (_lock)
        {
            if (_tablesInitialized) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                // สร้างตาราง ChatSessions ถ้ายังไม่มี
                var createSessionsTable = @"
                    IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ChatSessions' AND xtype='U')
                    CREATE TABLE ChatSessions (
                        SessionId    UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
                        EmployeeId   NVARCHAR(50) NOT NULL,
                        SessionTitle NVARCHAR(200) NOT NULL DEFAULT N'แชทใหม่',
                        CreatedAt    DATETIME NOT NULL DEFAULT GETDATE(),
                        UpdatedAt    DATETIME NOT NULL DEFAULT GETDATE(),
                        IsActive     BIT NOT NULL DEFAULT 1
                    );

                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='IX_ChatSessions_EmployeeId' AND object_id = OBJECT_ID('ChatSessions'))
                    CREATE INDEX IX_ChatSessions_EmployeeId ON ChatSessions(EmployeeId);

                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='IX_ChatSessions_UpdatedAt' AND object_id = OBJECT_ID('ChatSessions'))
                    CREATE INDEX IX_ChatSessions_UpdatedAt ON ChatSessions(UpdatedAt DESC);
                ";

                // สร้างตาราง ChatMessages ถ้ายังไม่มี
                var createMessagesTable = @"
                    IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='ChatMessages' AND xtype='U')
                    CREATE TABLE ChatMessages (
                        MessageId    BIGINT IDENTITY(1,1) PRIMARY KEY,
                        SessionId    UNIQUEIDENTIFIER NOT NULL,
                        EmployeeId   NVARCHAR(50) NOT NULL,
                        Role         NVARCHAR(20) NOT NULL,
                        Content      NVARCHAR(MAX) NOT NULL,
                        CreatedAt    DATETIME NOT NULL DEFAULT GETDATE(),
                        CONSTRAINT FK_ChatMessages_Session FOREIGN KEY (SessionId) REFERENCES ChatSessions(SessionId) ON DELETE CASCADE
                    );

                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='IX_ChatMessages_SessionId' AND object_id = OBJECT_ID('ChatMessages'))
                    CREATE INDEX IX_ChatMessages_SessionId ON ChatMessages(SessionId);

                    IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name='IX_ChatMessages_CreatedAt' AND object_id = OBJECT_ID('ChatMessages'))
                    CREATE INDEX IX_ChatMessages_CreatedAt ON ChatMessages(CreatedAt);
                ";

                conn.Execute(createSessionsTable);
                conn.Execute(createMessagesTable);

                _tablesInitialized = true;
                _logger.LogInformation("Chat history tables verified/created in HrChatAI database");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create chat history tables");
                throw;
            }
        }
    }

    public async Task<Guid> CreateSessionAsync(string employeeId)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                DECLARE @id UNIQUEIDENTIFIER = NEWID()
                INSERT INTO ChatSessions (SessionId, EmployeeId, SessionTitle)
                VALUES (@id, @EmployeeId, N'แชทใหม่')
                SELECT @id
            ";
            return await conn.QueryFirstOrDefaultAsync<Guid>(sql, new { EmployeeId = employeeId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateSession error for {EmployeeId}", employeeId);
            return Guid.NewGuid();
        }
    }

    public async Task SaveMessageAsync(Guid sessionId, string employeeId, string role, string content)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                INSERT INTO ChatMessages (SessionId, EmployeeId, Role, Content)
                VALUES (@SessionId, @EmployeeId, @Role, @Content);

                UPDATE ChatSessions 
                SET UpdatedAt = GETDATE()
                WHERE SessionId = @SessionId;

                -- อัปเดตชื่อ session จากข้อความแรกของผู้ใช้
                UPDATE ChatSessions
                SET SessionTitle = LEFT(@Content, 50)
                WHERE SessionId = @SessionId 
                  AND SessionTitle = N'แชทใหม่'
                  AND @Role = 'user';
            ";
            await conn.ExecuteAsync(sql, new { SessionId = sessionId, EmployeeId = employeeId, Role = role, Content = content });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveMessage error session={SessionId}", sessionId);
        }
    }

    public async Task<List<ChatSessionInfo>> GetSessionsAsync(string employeeId, int top = 20)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                SELECT TOP (@Top) 
                    SessionId, EmployeeId, SessionTitle, CreatedAt, UpdatedAt, IsActive
                FROM ChatSessions
                WHERE EmployeeId = @EmployeeId AND IsActive = 1
                ORDER BY UpdatedAt DESC
            ";
            var results = await conn.QueryAsync<ChatSessionInfo>(sql, new { EmployeeId = employeeId, Top = top });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSessions error for {EmployeeId}", employeeId);
            return new List<ChatSessionInfo>();
        }
    }

    public async Task<List<ChatMessageRecord>> GetMessagesAsync(Guid sessionId, string employeeId)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                SELECT m.MessageId, m.SessionId, m.EmployeeId, m.Role, m.Content, m.CreatedAt
                FROM ChatMessages m
                INNER JOIN ChatSessions s ON m.SessionId = s.SessionId
                WHERE m.SessionId = @SessionId 
                  AND s.EmployeeId = @EmployeeId
                ORDER BY m.CreatedAt ASC
            ";
            var results = await conn.QueryAsync<ChatMessageRecord>(sql, new { SessionId = sessionId, EmployeeId = employeeId });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMessages error session={SessionId}", sessionId);
            return new List<ChatMessageRecord>();
        }
    }

    public async Task<List<ChatMessageRecord>> GetAllMessagesByDateAsync(DateTime date, string? employeeId = null, int skip = 0, int take = 500)
    {
        try
        {
            skip = Math.Max(0, skip);
            take = Math.Clamp(take, 1, 200);

            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                SELECT m.MessageId, m.SessionId, m.EmployeeId, m.Role, m.Content, m.CreatedAt, m.TokensUsed
                FROM ChatMessages m
                WHERE m.CreatedAt >= @StartDate
                  AND m.CreatedAt < @EndDate
                  AND (@EmployeeId IS NULL OR m.EmployeeId = @EmployeeId)
                ORDER BY m.CreatedAt ASC, m.MessageId ASC
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY
            ";
            var results = await conn.QueryAsync<ChatMessageRecord>(sql, new
            {
                StartDate = date.Date,
                EndDate = date.Date.AddDays(1),
                EmployeeId = string.IsNullOrWhiteSpace(employeeId) ? null : employeeId.Trim(),
                Skip = skip,
                Take = take
            });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllMessagesByDate error date={Date} employeeId={EmployeeId}", date, employeeId);
            return new List<ChatMessageRecord>();
        }
    }

    public async Task DeleteSessionAsync(Guid sessionId, string employeeId)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            const string sql = @"
                UPDATE ChatSessions SET IsActive = 0
                WHERE SessionId = @SessionId AND EmployeeId = @EmployeeId
            ";
            await conn.ExecuteAsync(sql, new { SessionId = sessionId, EmployeeId = employeeId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteSession error session={SessionId}", sessionId);
        }
    }
}
