using Microsoft.Data.SqlClient;

namespace Rakit.Core.Data;

public interface IDbConnectionFactory
{
    /// <summary>Open connection running as database user <c>rakit_app</c>. Caller disposes.</summary>
    Task<SqlConnection> CreateMetadataAsync(CancellationToken ct = default);

    /// <summary>Open connection running as database user <c>rakit_exec</c>. Caller disposes.</summary>
    Task<SqlConnection> CreateExecutionAsync(CancellationToken ct = default);

    /// <summary>
    /// Open connection for the Playground, running as <c>rakit_play</c> (<paramref name="write"/>: full control of the app
    /// schema) or as <c>rakit_play_ro</c> (read and execute only). Uses the execution connection string. Caller disposes.
    /// </summary>
    Task<SqlConnection> CreatePlaygroundAsync(bool write, CancellationToken ct = default);
}
