using System.Data;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.Workflows;

/// <summary>The saved state of a run that is waiting for the visitor (rakit.WorkflowRuns).</summary>
/// <param name="StepCode">The Action step that is showing its form.</param>
/// <param name="State">The run's values as JSON (<see cref="WorkflowRunState"/>).</param>
public sealed record PendingRun(
    Guid RunId, Guid WorkflowId, Guid? ModuleId, string? ActionCode, string StepCode, string State, DateTime Expires);

/// <summary>What a paused run needs to go on: the form and key it started with, the claims, the clock and the outputs so far (as text).</summary>
public sealed class WorkflowRunState
{
    public Dictionary<string, string?> Form { get; set; } = [];
    public string? Key { get; set; }
    public Dictionary<string, string?> Claims { get; set; } = [];
    public DateTimeOffset Now { get; set; }
    public Dictionary<string, string?> Outputs { get; set; } = [];
    public string? RunBy { get; set; }
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
