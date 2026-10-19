using Microsoft.Data.SqlClient;

namespace Rakit.Core.Data;

public interface IDbConnectionFactory
{
    /// <summary>Open connection running as database user <c>rakit_app</c>. Caller disposes.</summary>
    Task<SqlConnection> CreateMetadataAsync(CancellationToken ct = default);

    /// <summary>Open connection running as database user <c>rakit_exec</c>. Caller disposes.</summary>
    Task<SqlConnection> CreateExecutionAsync(CancellationToken ct = default);
}
