namespace Rakit.Core.Health;

public interface IDbHealthService
{
    /// <summary>Runs SELECT 1 on the Metadata and Execution connections. Never throws for DB failures.</summary>
    Task<DbHealthReport> CheckAsync(CancellationToken ct = default);
}
