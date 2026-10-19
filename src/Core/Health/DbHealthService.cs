using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Rakit.Core.Data;

namespace Rakit.Core.Health;

public sealed class DbHealthService(IDbConnectionFactory connections, ILogger<DbHealthService> logger) : IDbHealthService
{
    private const string UserFacingError = "Tidak dapat terhubung ke database.";

    public async Task<DbHealthReport> CheckAsync(CancellationToken ct = default)
    {
        var metadata = await CheckOneAsync("Metadata", connections.CreateMetadataAsync, ct);
        var execution = await CheckOneAsync("Execution", connections.CreateExecutionAsync, ct);
        return new DbHealthReport([metadata, execution]);
    }

    private async Task<ConnectionHealth> CheckOneAsync(
        string name, Func<CancellationToken, Task<SqlConnection>> open, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var conn = await open(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return new ConnectionHealth(name, true, stopwatch.Elapsed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Detail goes to the log only; the report must not leak server or database names.
            logger.LogError(ex, "Database health check failed for {Connection} connection.", name);
            return new ConnectionHealth(name, false, stopwatch.Elapsed, UserFacingError);
        }
    }
}
