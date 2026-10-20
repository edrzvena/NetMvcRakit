using System.Data;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.Workflows;

/// <summary>
/// How a run stands, worked out from the latest row of each of its steps: any failure (or rollback) = <see cref="Failed"/>; else an
/// Action step still showing its form = <see cref="Waiting"/>, or <see cref="Expired"/> once <see cref="ActionStepConfig.Lifetime"/> has
/// passed; else <see cref="Success"/> (also for a run the visitor cancelled).
/// </summary>
public enum TrackingRunStatus { Success, Failed, Waiting, Expired }

public sealed record TrackingRun(
    Guid RunId, Guid WorkflowId, string WorkflowName, DateTime Started, string? ActionCode, string? RunBy, Guid? ModuleId,
    int StepCount, int DurationMs, TrackingRunStatus Status);

/// <param name="FromUtc">Inclusive: a run counts when its first step was written at or after this.</param>
/// <param name="ToUtc">Exclusive.</param>
public sealed record TrackingQuery(DateTime FromUtc, DateTime ToUtc, Guid? WorkflowId = null, TrackingRunStatus? Status = null);

/// <summary>One calendar day (server local time) of the runs in range; waiting counts <see cref="TrackingRunStatus.Waiting"/> and <see cref="TrackingRunStatus.Expired"/>.</summary>
public sealed record TrackingDay(DateOnly Date, int Success, int Failed, int Waiting);

/// <param name="Runs">Newest first.</param>
/// <param name="Truncated">There were more runs than <see cref="TrackingQueryService.MaxRuns"/>; the oldest were left out.</param>
public sealed record TrackingOverview(
    IReadOnlyList<TrackingRun> Runs, bool Truncated, int Success, int Failed, int Waiting, int Expired, int AverageDurationMs,
    IReadOnlyList<TrackingDay> Days);

public interface ITrackingQueryService
{
    /// <summary>The runs that started in the range (a range over <see cref="TrackingQueryService.MaxDays"/> days is cut to its last days), with counts and a per-day series.</summary>
    Task<TrackingOverview> QueryAsync(TrackingQuery query, CancellationToken ct = default);

    /// <summary>All rows of one run, oldest first (a step that waited and was then answered has two rows).</summary>
    Task<IReadOnlyList<TrackingEntry>> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class TrackingQueryService(IDbConnectionFactory connections) : ITrackingQueryService
{
    public const int MaxRuns = 5_000;
    public const int MaxDays = 92;

    private sealed class Row
    {
        public string? StepCode { get; set; }
        public string? StepName { get; set; }
        public string? Status { get; set; }
        public string? Message { get; set; }
        public int DurationMs { get; set; }
        public Guid? ModuleId { get; set; }
        public string? ActionCode { get; set; }
        public string? RunBy { get; set; }
    }

    public async Task<TrackingOverview> QueryAsync(TrackingQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var to = query.ToUtc;
        var from = query.FromUtc < to.AddDays(-MaxDays) ? to.AddDays(-MaxDays) : query.FromUtc;

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
WITH runs AS (
    SELECT TOP (@take) RunId
    FROM rakit.TrackingSteps
    WHERE (@workflow IS NULL OR WorkflowId = @workflow)
    GROUP BY RunId
    HAVING MIN(Created) >= @from AND MIN(Created) < @to
    ORDER BY MIN(Created) DESC
)
SELECT t.RunId, t.WorkflowId, ISNULL(w.Name, N''), t.Data, t.Created, t.Id
FROM rakit.TrackingSteps t
JOIN runs r ON r.RunId = t.RunId
LEFT JOIN rakit.Workflows w ON w.Id = t.WorkflowId
ORDER BY t.Created, t.Id";
        cmd.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = MaxRuns + 1 });
        cmd.Parameters.Add(new SqlParameter("@workflow", SqlDbType.UniqueIdentifier) { Value = (object?)query.WorkflowId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Value = from });
        cmd.Parameters.Add(new SqlParameter("@to", SqlDbType.DateTime2) { Value = to });

        var byRun = new Dictionary<Guid, List<(Guid WorkflowId, string WorkflowName, Row Data, DateTime Created)>>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var runId = reader.GetGuid(0);
                if (!byRun.TryGetValue(runId, out var list)) byRun[runId] = list = [];
                list.Add((reader.GetGuid(1), reader.GetString(2), WorkflowJson.DeserializeData<Row>(reader.GetString(3)) ?? new Row(), reader.GetDateTime(4)));
            }
        }

        var now = DateTime.UtcNow;
        var runs = byRun.Select(pair => Summarize(pair.Key, pair.Value, now)).OrderByDescending(r => r.Started).ToList();
        var truncated = runs.Count > MaxRuns;
        if (truncated) runs = runs.Take(MaxRuns).ToList();
        if (query.Status is { } wanted) runs = runs.Where(r => r.Status == wanted).ToList();

        var timeZone = TimeZoneInfo.Local;
        var perDay = new Dictionary<DateOnly, (int Success, int Failed, int Waiting)>();
        foreach (var run in runs)
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(run.Started, DateTimeKind.Utc), timeZone));
            perDay.TryGetValue(day, out var counts);
            perDay[day] = run.Status switch
            {
                TrackingRunStatus.Success => (counts.Success + 1, counts.Failed, counts.Waiting),
                TrackingRunStatus.Failed => (counts.Success, counts.Failed + 1, counts.Waiting),
                _ => (counts.Success, counts.Failed, counts.Waiting + 1),
            };
        }

        var days = new List<TrackingDay>();
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(from, DateTimeKind.Utc), timeZone));
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(to.AddTicks(-1), DateTimeKind.Utc), timeZone));
        for (var day = first; day <= last && days.Count <= MaxDays; day = day.AddDays(1))
        {
            perDay.TryGetValue(day, out var counts);
            days.Add(new TrackingDay(day, counts.Success, counts.Failed, counts.Waiting));
        }

        // an average over the runs that finished: a run that is still waiting has not taken any time yet
        var finished = runs.Where(r => r.Status is TrackingRunStatus.Success or TrackingRunStatus.Failed).ToList();
        return new TrackingOverview(
            runs, truncated,
            runs.Count(r => r.Status == TrackingRunStatus.Success), runs.Count(r => r.Status == TrackingRunStatus.Failed),
            runs.Count(r => r.Status == TrackingRunStatus.Waiting), runs.Count(r => r.Status == TrackingRunStatus.Expired),
            finished.Count == 0 ? 0 : (int)Math.Round(finished.Average(r => r.DurationMs)), days);
    }

    public async Task<IReadOnlyList<TrackingEntry>> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Id, RunId, WorkflowId, Data, Created
FROM rakit.TrackingSteps
WHERE RunId = @run
ORDER BY Created, Id";
        cmd.Parameters.Add(new SqlParameter("@run", SqlDbType.UniqueIdentifier) { Value = runId });

        var result = new List<TrackingEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var data = WorkflowJson.DeserializeData<Row>(reader.GetString(3)) ?? new Row();
            result.Add(new TrackingEntry(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), data.StepCode ?? "", data.StepName,
                WorkflowJson.TrackingFromText(data.Status), data.Message, data.DurationMs, data.ModuleId, data.ActionCode,
                data.RunBy, reader.GetDateTime(4)));
        }
        return result;
    }

    private static TrackingRun Summarize(
        Guid runId, List<(Guid WorkflowId, string WorkflowName, Row Data, DateTime Created)> rows, DateTime now)
    {
        // the latest row of each step decides: a step that waited and was then answered counts as answered
        var latest = rows.GroupBy(r => r.Data.StepCode ?? "").Select(g => g.OrderBy(r => r.Created).Last()).ToList();
        var status = TrackingRunStatus.Success;
        if (latest.Any(r => WorkflowJson.TrackingFromText(r.Data.Status) is TrackingStatus.Failed or TrackingStatus.RolledBack))
        {
            status = TrackingRunStatus.Failed;
        }
        else if (latest.Where(r => WorkflowJson.TrackingFromText(r.Data.Status) == TrackingStatus.Waiting).ToList() is { Count: > 0 } waiting)
        {
            status = now - waiting.Max(r => r.Created) > ActionStepConfig.Lifetime ? TrackingRunStatus.Expired : TrackingRunStatus.Waiting;
        }

        var head = rows[0];
        var any = rows.Select(r => r.Data).FirstOrDefault(d => d.ActionCode is not null || d.RunBy is not null || d.ModuleId is not null) ?? head.Data;
        return new TrackingRun(
            runId, head.WorkflowId, head.WorkflowName, rows.Min(r => r.Created), any.ActionCode, any.RunBy, any.ModuleId,
            rows.Count(r => WorkflowJson.TrackingFromText(r.Data.Status) != TrackingStatus.Waiting),
            rows.Where(r => WorkflowJson.TrackingFromText(r.Data.Status) != TrackingStatus.Waiting).Sum(r => r.Data.DurationMs), status);
    }
}
