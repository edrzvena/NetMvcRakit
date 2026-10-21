using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.Workflows;

/// <summary>The saved state of a run that is waiting for the visitor (rakit.WorkflowRuns).</summary>
/// <param name="StepCode">The Action step that is showing its form.</param>
/// <param name="State">The run's values as JSON (<see cref="WorkflowRunState"/>).</param>
public sealed record PendingRun(
    Guid RunId, Guid WorkflowId, Guid? ModuleId, string? ActionCode, string StepCode, string State, DateTime Expires);

/// <summary>What a paused run needs to go on: the form and key it started with, the claims, the clock and the outputs so far.</summary>
public sealed class WorkflowRunState
{
    public Dictionary<string, string?> Form { get; set; } = [];
    public string? Key { get; set; }
    public Dictionary<string, string?> Claims { get; set; } = [];
    public DateTimeOffset Now { get; set; }

    /// <summary>The outputs with their type, so the steps after the Action get the same values (a datetime2 to the tick, binary as bytes).</summary>
    public Dictionary<string, StoredValue> Values { get; set; } = [];

    /// <summary>The outputs as text: what runs saved before <see cref="Values"/> existed hold; read only when <see cref="Values"/> is empty.</summary>
    public Dictionary<string, string?> Outputs { get; set; } = [];

    public string? RunBy { get; set; }
}

/// <summary>A step output written so that it reads back as the same .NET value (round-trip formats, invariant culture).</summary>
public sealed class StoredValue
{
    public string Type { get; set; } = "string";
    public string? Text { get; set; }

    public static StoredValue From(object? value)
    {
        var c = CultureInfo.InvariantCulture;
        return value switch
        {
            null or DBNull => new StoredValue { Type = "null" },
            string s => new StoredValue { Text = s },
            bool b => new StoredValue { Type = "bool", Text = b ? "true" : "false" },
            byte n => new StoredValue { Type = "byte", Text = n.ToString(c) },
            short n => new StoredValue { Type = "int16", Text = n.ToString(c) },
            int n => new StoredValue { Type = "int32", Text = n.ToString(c) },
            long n => new StoredValue { Type = "int64", Text = n.ToString(c) },
            decimal n => new StoredValue { Type = "decimal", Text = n.ToString(c) },
            double n => new StoredValue { Type = "double", Text = n.ToString("R", c) },
            float n => new StoredValue { Type = "single", Text = n.ToString("R", c) },
            DateTime d => new StoredValue { Type = "datetime", Text = d.ToString("O", c) },
            DateTimeOffset d => new StoredValue { Type = "datetimeoffset", Text = d.ToString("O", c) },
            TimeSpan t => new StoredValue { Type = "timespan", Text = t.ToString("c", c) },
            Guid g => new StoredValue { Type = "guid", Text = g.ToString("D") },
            byte[] bytes => new StoredValue { Type = "bytes", Text = Convert.ToBase64String(bytes) },
            _ => new StoredValue { Text = WorkflowValues.ToText(value) },
        };
    }

    public object? ToValue()
    {
        if (Type == "null" || Text is null) return null;
        var c = CultureInfo.InvariantCulture;
        try
        {
            return Type switch
            {
                "bool" => Text == "true",
                "byte" => byte.Parse(Text, c),
                "int16" => short.Parse(Text, c),
                "int32" => int.Parse(Text, c),
                "int64" => long.Parse(Text, c),
                "decimal" => decimal.Parse(Text, NumberStyles.Number, c),
                "double" => double.Parse(Text, NumberStyles.Float, c),
                "single" => float.Parse(Text, NumberStyles.Float, c),
                "datetime" => DateTime.Parse(Text, c, DateTimeStyles.RoundtripKind),
                "datetimeoffset" => DateTimeOffset.Parse(Text, c, DateTimeStyles.RoundtripKind),
                "timespan" => TimeSpan.ParseExact(Text, "c", c),
                "guid" => Guid.Parse(Text),
                "bytes" => Convert.FromBase64String(Text),
                _ => Text,
            };
        }
        catch (FormatException)
        {
            return Text; // written by this class, so only a hand-edited row ends up here
        }
    }
}

public interface IWorkflowRunStore
{
    /// <summary>Saves the run as pending (a run that goes on to a second Action step is saved again under the same id). Also removes rows long expired.</summary>
    Task SaveAsync(PendingRun run, CancellationToken ct = default);

    /// <summary>The run if it is still pending and not expired; otherwise null.</summary>
    Task<PendingRun?> GetPendingAsync(Guid runId, CancellationToken ct = default);

    /// <summary>Marks a pending run as used. False when somebody else got there first, or it expired: the caller must not go on.</summary>
    Task<bool> ClaimAsync(Guid runId, CancellationToken ct = default);
}

public sealed class WorkflowRunStore(IDbConnectionFactory connections) : IWorkflowRunStore
{
    /// <summary>Rows are kept this long after they expire, then removed when a new one is saved.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    public async Task SaveAsync(PendingRun run, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);

        await using (var clean = conn.CreateCommand())
        {
            clean.CommandText = "DELETE FROM rakit.WorkflowRuns WHERE Expires < @limit";
            clean.Parameters.Add(new SqlParameter("@limit", SqlDbType.DateTime2) { Value = DateTime.UtcNow - Retention });
            await clean.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE rakit.WorkflowRuns
SET StepCode = @step, State = @state, Status = N'pending', Expires = @expires
WHERE RunId = @id;
IF @@ROWCOUNT = 0
    INSERT INTO rakit.WorkflowRuns (RunId, WorkflowId, ModuleId, ActionCode, StepCode, State, Status, Created, Expires)
    VALUES (@id, @workflow, @module, @action, @step, @state, N'pending', @created, @expires);";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = run.RunId });
        cmd.Parameters.Add(new SqlParameter("@workflow", SqlDbType.UniqueIdentifier) { Value = run.WorkflowId });
        cmd.Parameters.Add(new SqlParameter("@module", SqlDbType.UniqueIdentifier) { Value = (object?)run.ModuleId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@action", SqlDbType.NVarChar, 50) { Value = (object?)run.ActionCode ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@step", SqlDbType.NVarChar, 50) { Value = run.StepCode });
        cmd.Parameters.Add(new SqlParameter("@state", SqlDbType.NVarChar, -1) { Value = run.State });
        cmd.Parameters.Add(new SqlParameter("@created", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        cmd.Parameters.Add(new SqlParameter("@expires", SqlDbType.DateTime2) { Value = run.Expires });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PendingRun?> GetPendingAsync(Guid runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT WorkflowId, ModuleId, ActionCode, StepCode, State, Expires
FROM rakit.WorkflowRuns
WHERE RunId = @id AND Status = N'pending' AND Expires > @now";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = runId });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PendingRun(
            runId, reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetDateTime(5));
    }

    public async Task<bool> ClaimAsync(Guid runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rakit.WorkflowRuns SET Status = N'done' WHERE RunId = @id AND Status = N'pending' AND Expires > @now";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = runId });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
