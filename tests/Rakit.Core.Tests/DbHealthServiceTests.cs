using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.Health;

namespace Rakit.Core.Tests;

[Collection(DbCollection.Name)]
public class DbHealthServiceTests
{
    private static DbHealthService Create(RakitDbOptions options)
        => new(new DbConnectionFactory(Options.Create(options)), NullLogger<DbHealthService>.Instance);

    [Fact]
    public async Task Healthy_when_both_connections_work()
    {
        var report = await Create(TestDb.Options).CheckAsync();

        Assert.True(report.IsHealthy);
        Assert.Equal(["Metadata", "Execution"], report.Connections.Select(c => c.Name));
        Assert.All(report.Connections, c =>
        {
            Assert.True(c.IsHealthy);
            Assert.True(c.Latency > TimeSpan.Zero);
            Assert.Null(c.Error);
        });
    }

    [Fact]
    public async Task Unhealthy_when_execution_connection_string_is_wrong()
    {
        var options = TestDb.Options;
        options.ExecutionConnectionString =
            $"{TestDb.BaseConnectionString};Database=Rakit_NoSuchDb;Connect Timeout=3";

        var report = await Create(options).CheckAsync();

        Assert.False(report.IsHealthy);
        Assert.True(report.Connections.Single(c => c.Name == "Metadata").IsHealthy);
        var execution = report.Connections.Single(c => c.Name == "Execution");
        Assert.False(execution.IsHealthy);
        Assert.False(string.IsNullOrWhiteSpace(execution.Error));
    }

    [Fact]
    public async Task Error_does_not_leak_connection_details()
    {
        var options = TestDb.Options;
        options.MetadataConnectionString =
            $"{TestDb.BaseConnectionString};Database=Rakit_NoSuchDb;Connect Timeout=3";
        options.ExecutionConnectionString = options.MetadataConnectionString;

        var report = await Create(options).CheckAsync();

        foreach (var c in report.Connections)
        {
            Assert.False(c.IsHealthy);
            Assert.DoesNotContain("Server=", c.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SQLEXPRESS01", c.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Rakit_NoSuchDb", c.Error!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Cancellation_is_propagated_not_reported_as_unhealthy()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(TestDb.Options).CheckAsync(cts.Token));
    }
}
