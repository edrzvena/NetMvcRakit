using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

namespace Rakit.Core.Workflows;

public enum WorkflowFailureKind
{
    None,

    /// <summary>The database refused on purpose (a THROW message, a value that does not fit): the message is for the user.</summary>
    Business,

    /// <summary>A procedure returned a non-zero code (its step was rolled back).</summary>
    Rejected,

    /// <summary>The step cannot run because of how it is set up (data source gone or switched off, bad mapping). Generic message.</summary>
    Unavailable,

    Timeout,

    /// <summary>Anything else; logged, generic message.</summary>
    Error,
}

/// <param name="Message">Safe to show to end users.</param>
public sealed record WorkflowStepResult(string Code, string Name, TrackingStatus Status, string? Message, int DurationMs);

public sealed record WorkflowRunResult(
    Guid RunId, Guid WorkflowId, bool Success, WorkflowFailureKind Failure, string Message, IReadOnlyList<WorkflowStepResult> Steps);

/// <param name="Form">Values handed to the steps as FormField mappings (already validated by the caller).</param>
/// <param name="Key">Record key handed to the steps as RouteValue <c>id</c>, or null.</param>
/// <param name="Request">Only its claims and clock are used; the request's query string, form and route are not passed on.</param>
public sealed record WorkflowRunContext(
    ParameterContext Request, IReadOnlyDictionary<string, string?> Form, string? Key = null, Guid? ModuleId = null,
    string? ActionCode = null, string? RunBy = null);

public interface IWorkflowRunner
{
    /// <summary>
    /// Runs the steps of a published workflow in order, each as its own data source call (a procedure's changes are kept
    /// only when it returns 0). The first step that fails stops the run; the steps after it are recorded as skipped. A step's
    /// outputs are available to the later steps as StepOutput values: <c>{code}.return</c>, <c>{code}.out.{Parameter}</c>
    /// and <c>{code}.row.{Column}</c> (first row of the step's result). Every step is written to rakit.TrackingSteps.
    /// A failing step is reported in the result, not thrown.
    /// </summary>
    /// <exception cref="WorkflowNotFoundException">No such workflow, deleted, or still a draft.</exception>
    Task<WorkflowRunResult> RunAsync(Guid workflowId, WorkflowRunContext context, CancellationToken ct = default);
}

public sealed class WorkflowRunner(
    IWorkflowRepository workflows, IDataSourceService dataSources, ILogger<WorkflowRunner> logger) : IWorkflowRunner
{
    public async Task<WorkflowRunResult> RunAsync(Guid workflowId, WorkflowRunContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workflow = await workflows.GetAsync(workflowId, ct);
        if (workflow is null || workflow.Status != WorkflowStatus.Published) throw new WorkflowNotFoundException(workflowId);

        var runId = Guid.NewGuid();
        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<WorkflowStepResult>();
        var tracking = new List<TrackingEntry>();
        var failure = WorkflowFailureKind.None;
        var message = "";

        void Track(WorkflowStep step, TrackingStatus status, string? text, int ms) => tracking.Add(new TrackingEntry(
            Guid.NewGuid(), runId, workflow.Id, step.Code, step.DisplayName, status, text, ms, context.ModuleId, context.ActionCode,
            context.RunBy, DateTime.UtcNow));

        foreach (var step in workflow.Steps)
        {
            if (failure != WorkflowFailureKind.None)
            {
                results.Add(new WorkflowStepResult(step.Code, step.DisplayName, TrackingStatus.Skipped, null, 0));
                Track(step, TrackingStatus.Skipped, null, 0);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var (kind, text) = await RunStepAsync(workflow, step, context, outputs, ct);
            var ms = (int)stopwatch.ElapsedMilliseconds;

            if (kind == WorkflowFailureKind.None)
            {
                results.Add(new WorkflowStepResult(step.Code, step.DisplayName, TrackingStatus.Success, null, ms));
                Track(step, TrackingStatus.Success, null, ms);
            }
            else
            {
                failure = kind;
                message = kind is WorkflowFailureKind.Business or WorkflowFailureKind.Rejected
                    ? $"Langkah '{step.DisplayName}' gagal: {text}"
                    : text;
                results.Add(new WorkflowStepResult(step.Code, step.DisplayName, TrackingStatus.Failed, text, ms));
                Track(step, TrackingStatus.Failed, text, ms);
            }
        }

        try
        {
            await workflows.AddTrackingAsync(tracking, CancellationToken.None);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            // the run itself is done and its changes are committed: losing the history rows must not turn it into a failure
            logger.LogError(ex, "Workflow {WorkflowCode} run {RunId}: tracking could not be written.", workflow.Code, runId);
        }

        return failure == WorkflowFailureKind.None
            ? new WorkflowRunResult(runId, workflow.Id, true, failure, $"Workflow '{workflow.Name}' selesai.", results)
            : new WorkflowRunResult(runId, workflow.Id, false, failure, message, results);
    }

    /// <summary>Runs one step; on success its outputs are added to <paramref name="outputs"/>. The text is safe for end users.</summary>
    private async Task<(WorkflowFailureKind Kind, string Text)> RunStepAsync(
        Workflow workflow, WorkflowStep step, WorkflowRunContext context, Dictionary<string, object?> outputs, CancellationToken ct)
    {
        var parameters = new ParameterContext
        {
            Form = new Dictionary<string, string?>(context.Form, StringComparer.OrdinalIgnoreCase),
            Route = context.Key is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["id"] = context.Key },
            Claims = context.Request.Claims,
            Now = context.Request.Now,
            StepOutputs = new Dictionary<string, object?>(outputs, StringComparer.OrdinalIgnoreCase),
        };

        DataSourceResult result;
        try
        {
            result = await dataSources.ExecuteAsync(
                step.DataSourceId, parameters, new DataSourceRunRequest(0, 1, IncludeTotal: false, CommitOnlyIfReturnValueZero: true), ct);
        }
        catch (SqlExecutionException ex)
        {
            switch (ex.Kind)
            {
                case SqlExecErrorKind.Business:
                    return (WorkflowFailureKind.Business, ex.Message);
                case SqlExecErrorKind.Timeout:
                    logger.LogWarning(ex, "Workflow {WorkflowCode} step {Step} timed out.", workflow.Code, step.Code);
                    return (WorkflowFailureKind.Timeout, "Langkah memakan waktu terlalu lama dan dihentikan.");
                default:
                    logger.LogError(ex, "Workflow {WorkflowCode} step {Step} failed in the database: {Detail}", workflow.Code, step.Code, ex.Detail);
                    return (WorkflowFailureKind.Error, "Langkah gagal dijalankan.");
            }
        }
        catch (Exception ex) when (ex is DataSourceNotFoundException or DataSourceForbiddenException or ParameterResolutionException
            or TableDefinitionException or SqlTemplateException or SchemaAccessDeniedException or SchemaObjectNotFoundException
            or InvalidOperationException)
        {
            logger.LogWarning(ex, "Workflow {WorkflowCode} step {Step} cannot run: {Reason}", workflow.Code, step.Code, ex.Message);
            return (WorkflowFailureKind.Unavailable, "Aksi ini tidak tersedia saat ini.");
        }
        catch (ArgumentException ex)
        {
            // a value the data source's parameter cannot take (wrong type, too long)
            return (WorkflowFailureKind.Business, ExceptionMessages.WithoutParameterSuffix(ex));
        }

        if (result.ReturnValue is { } code and not 0)
        {
            logger.LogInformation("Workflow {WorkflowCode} step {Step} was rejected by its procedure with code {Code}.", workflow.Code, step.Code, code);
            return (WorkflowFailureKind.Rejected, $"Operasi ditolak (kode {code}).");
        }

        // outputs for the later steps: {code}.return, {code}.out.{Parameter}, {code}.row.{Column}
        var prefix = step.Code + ".";
        if (result.ReturnValue is { } returned) outputs[prefix + "return"] = returned;
        foreach (var (name, value) in result.OutputParameters) outputs[prefix + "out." + name] = value;
        if (result.List is { Rows.Count: > 0 } list)
        {
            var row = list.Rows[0];
            for (var i = 0; i < list.Columns.Count && i < row.Count; i++) outputs[prefix + "row." + list.Columns[i]] = row[i];
        }
        return (WorkflowFailureKind.None, "");
    }
}
