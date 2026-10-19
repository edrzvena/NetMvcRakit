using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;

namespace Rakit.Core.Data;

public sealed class DbConnectionFactory(IOptions<RakitDbOptions> options) : IDbConnectionFactory
{
    // Constants on purpose: these are never built from input.
    private const string MetadataUser = "rakit_app";
    private const string ExecutionUser = "rakit_exec";

    public Task<SqlConnection> CreateMetadataAsync(CancellationToken ct = default)
        => OpenAsImpersonatedAsync(options.Value.MetadataConnectionString, MetadataUser, ct);

    public Task<SqlConnection> CreateExecutionAsync(CancellationToken ct = default)
        => OpenAsImpersonatedAsync(options.Value.ExecutionConnectionString, ExecutionUser, ct);

    private static async Task<SqlConnection> OpenAsImpersonatedAsync(string connectionString, string user, CancellationToken ct)
    {
        // A NO REVERT context cannot survive connection pool reset (and would leak identity to the next
        // pool user), so pooling is forced off regardless of configuration.
        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        var conn = new SqlConnection(builder.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EXECUTE AS USER = N'{user}' WITH NO REVERT;";
            await cmd.ExecuteNonQueryAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
