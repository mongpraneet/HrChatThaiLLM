using Dapper;
using Microsoft.Data.SqlClient;

namespace HrChatThaiLLM.Server.Services;

public class SqlExecutorService : ISqlExecutorService
{
    private readonly string _connectionString;
    private readonly ILogger<SqlExecutorService> _logger;

    public SqlExecutorService(IConfiguration config, ILogger<SqlExecutorService> logger)
    {
        _logger = logger;
        _connectionString = config.GetConnectionString("HrDatabase")
            ?? throw new InvalidOperationException("HrDatabase connection string not found");
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<T>(sql, param);
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.QueryAsync<T>(sql, param);
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        using var conn = new SqlConnection(_connectionString);
        return await conn.ExecuteAsync(sql, param);
    }
}