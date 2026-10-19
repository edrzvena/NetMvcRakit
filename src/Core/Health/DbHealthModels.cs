namespace Rakit.Core.Health;

public sealed record ConnectionHealth(string Name, bool IsHealthy, TimeSpan Latency, string? Error);

public sealed record DbHealthReport(IReadOnlyList<ConnectionHealth> Connections)
{
    public bool IsHealthy => Connections.All(c => c.IsHealthy);
}
