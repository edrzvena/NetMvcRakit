using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

namespace Rakit.Core.Workflows;

public enum WorkflowFailureKind
{
    None,

    /// <summary>The database or the called service refused on purpose (a THROW message, a value that does not fit, an HTTP error answer): the message is for the user.</summary>
    Business,

    /// <summary>A procedure returned a non-zero code (its changes were rolled back).</summary>
    Rejected,

    /// <summary>The step cannot run because of how it is set up (data source gone or switched off, host not allowed, bad mapping). Generic message.</summary>
    Unavailable,

    Timeout,

    /// <summary>Anything else; logged, generic message.</summary>
    Error,
}

/// <param name="Message">Safe to show to end users: why the step failed, or a note about a successful one (e.g. an email that was only logged).</param>
public sealed record WorkflowStepResult(string Code, string Name, TrackingStatus Status, string? Message, int DurationMs);

/// <summary>
/// What the visitor's browser should do after a successful run: <c>html</c> (show sanitized HTML), <c>module</c> (go to the URL)
/// or <c>form</c> (show the Action step's <see cref="Prompt"/>; the run waits for a button).
/// </summary>
public sealed record WorkflowDisplay(string Kind, string? Html, string? Url, WorkflowPrompt? Prompt = null);

/// <summary>The form of an Action step as the visitor sees it (title and intro already have their placeholders filled in).</summary>
public sealed record WorkflowPrompt(
    Guid RunId, string Title, string? Intro, IReadOnlyList<ModuleFormField> Fields, IReadOnlyList<ActionButton> Buttons);

/// <param name="RolledBack">The workflow's SQL transaction was rolled back (a step failed), undoing its earlier database steps.</param>
public sealed record WorkflowRunResult(
    Guid RunId, Guid WorkflowId, bool Success, WorkflowFailureKind Failure, string Message, IReadOnlyList<WorkflowStepResult> Steps,
    WorkflowDisplay? Display = null, bool RolledBack = false);

/// <param name="Form">Values handed to the steps as FormField mappings and <c>{{form.X}}</c> (already validated by the caller).</param>
/// <param name="Key">Record key handed to the steps as RouteValue <c>id</c> and <c>{{key}}</c>, or null.</param>
/// <param name="Request">Only its claims and clock are used; the request's query string, form and route are not passed on.</param>
public sealed record WorkflowRunContext(
    ParameterContext Request, IReadOnlyDictionary<string, string?> Form, string? Key = null, Guid? ModuleId = null,
    string? ActionCode = null, string? RunBy = null);

public interface IWorkflowRunner
{
    /// <summary>
    /// Runs a published workflow from its first step, following the links (a Condition picks a branch) until a step has no next
    /// step. DataSource steps are separate transactions (a procedure's changes are kept only when it returns 0), or, when the
    /// workflow asks for it, one shared SQL transaction that is rolled back as a whole if any step fails. The first failing step
    /// stops the run; the steps that would have followed are recorded as skipped. A step's outputs are available to the later
    /// steps as <c>{{code.row.Column}}</c>, <c>{{code.out.Parameter}}</c>, <c>{{code.return}}</c>, <c>{{code.status}}</c>,
    /// <c>{{code.json.path}}</c> in step texts, and as StepOutput values in data source mappings. Every step is written to
    /// rakit.TrackingSteps. A failing step is reported in the result, not thrown.
    /// </summary>
    /// <exception cref="WorkflowNotFoundException">No such workflow, deleted, or still a draft.</exception>
    Task<WorkflowRunResult> RunAsync(Guid workflowId, WorkflowRunContext context, CancellationToken ct = default);

    /// <summary>
    /// Goes on with a run that an Action step paused, after the visitor pressed <paramref name="buttonCode"/>. The values are
    /// validated with the Action's field rules (a cancel button needs none) and the run goes on from the step after the Action
    /// under the same <c>RunId</c>; a cancel button ends it as a success. The result can be another form (a second Action step).
    /// </summary>
    /// <exception cref="WorkflowRunNotFoundException">
    /// The run does not exist, belongs to another module, expired, was already used, or its workflow is gone or not published.
    /// </exception>
    /// <exception cref="ModuleRecordValidationException">A value is invalid or the button is unknown; the run stays pending.</exception>
    Task<WorkflowRunResult> ResumeAsync(
        Guid runId, Guid? moduleId, string buttonCode, IReadOnlyDictionary<string, string?> submitted, CancellationToken ct = default);
}

public sealed class WorkflowRunner(
    IWorkflowRepository workflows, IDataSourceService dataSources, ISqlExecutionService sql, IRestCaller rest,
    IWorkflowEmailSender email, IModuleLinkResolver moduleLinks, IWorkflowScriptEngine scripts, IWorkflowRunStore runStore,
    ILogger<WorkflowRunner> logger) : IWorkflowRunner
{
    private const string GenericUnavailable = "Aksi ini tidak tersedia saat ini.";

    /// <summary>What one step did. <see cref="Condition"/> is the answer of a Condition step.</summary>
    private sealed record Outcome(WorkflowFailureKind Kind, string Text, bool Condition = false, WorkflowDisplay? Display = null)
    {
        public static Outcome Ok(string? note = null, bool condition = false, WorkflowDisplay? display = null)
            => new(WorkflowFailureKind.None, note ?? "", condition, display);

        public static Outcome Fail(WorkflowFailureKind kind, string text) => new(kind, text);
    }

    private sealed class Record(WorkflowStep step, TrackingStatus status, string? text, int ms)
    {
        public WorkflowStep Step { get; } = step;
        public TrackingStatus Status { get; set; } = status;
        public string? Text { get; set; } = text;
        public int Ms { get; } = ms;
    }

    public async Task<WorkflowRunResult> RunAsync(Guid workflowId, WorkflowRunContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workflow = await workflows.GetAsync(workflowId, ct);
        if (workflow is null || workflow.Status != WorkflowStatus.Published) throw new WorkflowNotFoundException(workflowId);

        var values = new WorkflowValues(context.Form, context.Key, context.Request.Claims, context.Request.Now);
        var first = workflow.Steps.Count > 0 ? workflow.Steps[0] : null;
        return await ContinueAsync(workflow, Guid.NewGuid(), context, values, first, [], ct);
    }

    public async Task<WorkflowRunResult> ResumeAsync(
        Guid runId, Guid? moduleId, string buttonCode, IReadOnlyDictionary<string, string?> submitted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        var pending = await runStore.GetPendingAsync(runId, ct) ?? throw new WorkflowRunNotFoundException(runId);
        if (pending.ModuleId != moduleId) throw new WorkflowRunNotFoundException(runId);

        var workflow = await workflows.GetAsync(pending.WorkflowId, ct);
        if (workflow is null || workflow.Status != WorkflowStatus.Published) throw new WorkflowRunNotFoundException(runId);
        var step = workflow.Steps.FirstOrDefault(s => s.Type == StepType.Action && s.Code.Equals(pending.StepCode, StringComparison.OrdinalIgnoreCase));
        if (step?.Action is not { } action) throw new WorkflowRunNotFoundException(runId);

        var code = buttonCode?.Trim() ?? "";
        var button = action.Buttons.FirstOrDefault(b => b.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModuleRecordValidationException(new Dictionary<string, string> { [""] = "Tombol tidak dikenal." });

        // A cancel button needs no valid values. Invalid values leave the run pending so the visitor can correct them.
        var form = button.Cancel
            ? new Dictionary<string, string?>()
            : ModuleRecordService.ValidateFields(action.Fields, submitted, forInsert: true);

        if (!await runStore.ClaimAsync(runId, ct)) throw new WorkflowRunNotFoundException(runId);

        var state = WorkflowJson.DeserializeData<WorkflowRunState>(pending.State) ?? new WorkflowRunState();
        var values = new WorkflowValues(state.Form, state.Key, state.Claims, state.Now);
        foreach (var (key, value) in state.Outputs) values.Outputs[key] = value;
        values.Outputs[step.Code + ".button"] = button.Code.Trim();
        foreach (var (name, value) in form) values.Outputs[step.Code + ".form." + name] = value;

        var context = new WorkflowRunContext(
            new ParameterContext { Claims = state.Claims, Now = state.Now }, state.Form, state.Key, pending.ModuleId, pending.ActionCode, state.RunBy);

        var answered = new Record(step, TrackingStatus.Success, button.Cancel ? "Dibatalkan oleh pengunjung" : $"Tombol '{button.Label.Trim()}'", 0);
        if (button.Cancel)
        {
            await WriteTrackingAsync(workflow, runId, context, [answered]);
            return new WorkflowRunResult(
                runId, workflow.Id, true, WorkflowFailureKind.None, $"Workflow '{workflow.Name}' dibatalkan.",
                [new WorkflowStepResult(step.Code, step.DisplayName, TrackingStatus.Success, answered.Text, 0)]);
        }

        var index = WorkflowGraph.Index(workflow.Steps);
        var next = WorkflowGraph.NextOf(step, false);
        var start = next is not null && index.TryGetValue(next, out var found) ? found : null;
        return await ContinueAsync(workflow, runId, context, values, start, [answered], ct);
    }

    /// <summary>
    /// Runs from <paramref name="start"/> until a step has no next step, a step fails, or an Action step pauses the run. Writes the
    /// tracking rows of <paramref name="records"/> and of the steps it ran.
    /// </summary>
    private async Task<WorkflowRunResult> ContinueAsync(
        Workflow workflow, Guid runId, WorkflowRunContext context, WorkflowValues values, WorkflowStep? start,
        List<Record> records, CancellationToken ct)
    {
        var index = WorkflowGraph.Index(workflow.Steps);
        WorkflowDisplay? display = null;
        var failure = WorkflowFailureKind.None;
        var message = "";
        WorkflowStep? failedStep = null;
        var paused = false;

        ISqlScope? scope = null;
        if (workflow.Transaction && workflow.Steps.Any(s => s.Type == StepType.DataSource))
        {
            try
            {
                scope = await sql.BeginScopeAsync(ct);
            }
            catch (SqlExecutionException ex)
            {
                logger.LogError(ex, "Workflow {WorkflowCode}: the transaction could not be started: {Detail}", workflow.Code, ex.Detail);
                return new WorkflowRunResult(runId, workflow.Id, false, WorkflowFailureKind.Error, "Workflow gagal dijalankan.", []);
            }
        }

        var rolledBack = false;
        try
        {
            var current = start;
            for (var guard = 0; current is not null && guard <= workflow.Steps.Count; guard++)
            {
                if (current.Type == StepType.Action)
                {
                    var prompt = await PauseAsync(workflow, current, runId, context, values, ct);
                    if (prompt is null)
                    {
                        failure = WorkflowFailureKind.Error;
                        failedStep = current;
                        message = "Form tidak dapat ditampilkan.";
                        records.Add(new Record(current, TrackingStatus.Failed, message, 0));
                    }
                    else
                    {
                        records.Add(new Record(current, TrackingStatus.Waiting, "Menunggu jawaban pengunjung", 0));
                        display = new WorkflowDisplay("form", null, null, prompt);
                        paused = true;
                    }
                    break;
                }

                var stopwatch = Stopwatch.StartNew();
                var outcome = await RunStepAsync(workflow, current, context, values, scope, ct);
                var ms = (int)stopwatch.ElapsedMilliseconds;

                if (outcome.Kind != WorkflowFailureKind.None)
                {
                    failure = outcome.Kind;
                    failedStep = current;
                    message = outcome.Kind is WorkflowFailureKind.Business or WorkflowFailureKind.Rejected
                        ? $"Langkah '{current.DisplayName}' gagal: {outcome.Text}"
                        : outcome.Text;
                    records.Add(new Record(current, TrackingStatus.Failed, outcome.Text, ms));
                    break;
                }

                records.Add(new Record(current, TrackingStatus.Success, string.IsNullOrEmpty(outcome.Text) ? null : outcome.Text, ms));
                if (outcome.Display is not null) display = outcome.Display;

                var next = WorkflowGraph.NextOf(current, outcome.Condition);
                current = next is not null && index.TryGetValue(next, out var found) ? found : null;
            }

            // The steps that would have followed a failed one are recorded as skipped.
            if (failedStep is not null)
                foreach (var code in WorkflowGraph.ReachableFrom(workflow.Steps, failedStep.Code, includeStart: false))
                    if (index.TryGetValue(code, out var skipped) && records.All(r => !ReferenceEquals(r.Step, skipped)))
                        records.Add(new Record(skipped, TrackingStatus.Skipped, null, 0));

            if (scope is not null)
            {
                if (failure == WorkflowFailureKind.None)
                {
                    try
                    {
                        await scope.CommitAsync(ct);
                    }
                    catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
                    {
                        logger.LogError(ex, "Workflow {WorkflowCode}: the transaction could not be committed.", workflow.Code);
                        failure = WorkflowFailureKind.Error;
                        message = "Perubahan database tidak dapat disimpan.";
                        rolledBack = true;
                    }
                }
                else
                {
                    await scope.RollbackAsync();
                    rolledBack = true;
                }
            }
        }
        finally
        {
            if (scope is not null) await scope.DisposeAsync();
        }

        if (rolledBack)
        {
            var undone = false;
            foreach (var record in records.Where(r => r.Status == TrackingStatus.Success && r.Step.Type == StepType.DataSource))
            {
                record.Status = TrackingStatus.RolledBack;
                record.Text = "Perubahan dibatalkan (rollback)";
                undone = true;
            }
            if (undone) message += " Perubahan database dari workflow ini dibatalkan.";
        }

        await WriteTrackingAsync(workflow, runId, context, records);

        var steps = records.Select(r => new WorkflowStepResult(r.Step.Code, r.Step.DisplayName, r.Status, r.Text, r.Ms)).ToList();
        if (failure != WorkflowFailureKind.None)
            return new WorkflowRunResult(runId, workflow.Id, false, failure, message, steps, null, rolledBack);
        return new WorkflowRunResult(
            runId, workflow.Id, true, failure,
            paused ? $"Workflow '{workflow.Name}' menunggu jawaban." : $"Workflow '{workflow.Name}' selesai.", steps, display);
    }

    private async Task WriteTrackingAsync(Workflow workflow, Guid runId, WorkflowRunContext context, IReadOnlyList<Record> records)
    {
        var tracking = records.Select(r => new TrackingEntry(
            Guid.NewGuid(), runId, workflow.Id, r.Step.Code, r.Step.DisplayName, r.Status, r.Text, r.Ms, context.ModuleId,
            context.ActionCode, context.RunBy, DateTime.UtcNow)).ToList();
        try
        {
            await workflows.AddTrackingAsync(tracking, CancellationToken.None);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            // the run itself is done: losing the history rows must not turn it into a failure
            logger.LogError(ex, "Workflow {WorkflowCode} run {RunId}: tracking could not be written.", workflow.Code, runId);
        }
    }

    /// <summary>Saves the run's state and builds the form the visitor sees; null when the state cannot be saved.</summary>
    private async Task<WorkflowPrompt?> PauseAsync(
        Workflow workflow, WorkflowStep step, Guid runId, WorkflowRunContext context, WorkflowValues values, CancellationToken ct)
    {
        var action = step.Action;
        if (action is null) return null;

        var state = new WorkflowRunState
        {
            Form = new Dictionary<string, string?>(values.Form), Key = values.Key, Claims = new Dictionary<string, string?>(values.Claims),
            Now = values.Now, RunBy = context.RunBy,
        };
        foreach (var (key, value) in values.Outputs) state.Outputs[key] = WorkflowValues.ToText(value);

        try
        {
            await runStore.SaveAsync(new PendingRun(
                runId, workflow.Id, context.ModuleId, context.ActionCode, step.Code, WorkflowJson.SerializeData(state),
                DateTime.UtcNow + ActionStepConfig.Lifetime), ct);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            logger.LogError(ex, "Workflow {WorkflowCode} run {RunId}: the waiting state could not be saved.", workflow.Code, runId);
            return null;
        }

        return new WorkflowPrompt(
            runId, WorkflowTemplate.Expand(action.Title, values.Get).Trim(),
            string.IsNullOrWhiteSpace(action.Intro) ? null : WorkflowTemplate.Expand(action.Intro, values.Get).Trim(),
            action.Fields, action.Buttons);
    }

    // ---- one step ----------------------------------------------------------------------------------

    private async Task<Outcome> RunStepAsync(
        Workflow workflow, WorkflowStep step, WorkflowRunContext context, WorkflowValues values, ISqlScope? scope, CancellationToken ct)
    {
        try
        {
            return step.Type switch
            {
                StepType.DataSource => await RunDataSourceAsync(workflow, step, values, scope, ct),
                StepType.Rest => await RunRestAsync(workflow, step, values, ct),
                StepType.Email => await RunEmailAsync(workflow, step, values, ct),
                StepType.Page => RunPage(step, values),
                StepType.Module => await RunModuleAsync(workflow, step, values, ct),
                StepType.Condition => RunCondition(step, values),
                StepType.Javascript => RunScript(workflow, step, values),
                _ => Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow {WorkflowCode} step {Step} failed unexpectedly.", workflow.Code, step.Code);
            return Outcome.Fail(WorkflowFailureKind.Error, "Langkah gagal dijalankan.");
        }
    }

    private async Task<Outcome> RunDataSourceAsync(Workflow workflow, WorkflowStep step, WorkflowValues values, ISqlScope? scope, CancellationToken ct)
    {
        DataSourceResult result;
        try
        {
            result = await dataSources.ExecuteAsync(
                step.DataSourceId, values.ToParameterContext(),
                new DataSourceRunRequest(0, 1, IncludeTotal: false, CommitOnlyIfReturnValueZero: scope is null, Scope: scope), ct);
        }
        catch (SqlExecutionException ex)
        {
            switch (ex.Kind)
            {
                case SqlExecErrorKind.Business:
                    return Outcome.Fail(WorkflowFailureKind.Business, ex.Message);
                case SqlExecErrorKind.Timeout:
                    logger.LogWarning(ex, "Workflow {WorkflowCode} step {Step} timed out.", workflow.Code, step.Code);
                    return Outcome.Fail(WorkflowFailureKind.Timeout, "Langkah memakan waktu terlalu lama dan dihentikan.");
                default:
                    logger.LogError(ex, "Workflow {WorkflowCode} step {Step} failed in the database: {Detail}", workflow.Code, step.Code, ex.Detail);
                    return Outcome.Fail(WorkflowFailureKind.Error, "Langkah gagal dijalankan.");
            }
        }
        catch (Exception ex) when (ex is DataSourceNotFoundException or DataSourceForbiddenException or ParameterResolutionException
            or TableDefinitionException or SqlTemplateException or SchemaAccessDeniedException or SchemaObjectNotFoundException
            or InvalidOperationException)
        {
            logger.LogWarning(ex, "Workflow {WorkflowCode} step {Step} cannot run: {Reason}", workflow.Code, step.Code, ex.Message);
            return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);
        }
        catch (ArgumentException ex)
        {
            // a value the data source's parameter cannot take (wrong type, too long)
            return Outcome.Fail(WorkflowFailureKind.Business, ExceptionMessages.WithoutParameterSuffix(ex));
        }

        if (result.ReturnValue is { } code and not 0)
        {
            logger.LogInformation("Workflow {WorkflowCode} step {Step} was rejected by its procedure with code {Code}.", workflow.Code, step.Code, code);
            return Outcome.Fail(WorkflowFailureKind.Rejected, $"Operasi ditolak (kode {code}).");
        }

        // outputs for the later steps: {code}.return, {code}.out.{Parameter}, {code}.row.{Column}
        var prefix = step.Code + ".";
        if (result.ReturnValue is { } returned) values.Outputs[prefix + "return"] = returned;
        foreach (var (name, value) in result.OutputParameters) values.Outputs[prefix + "out." + name] = value;
        if (result.List is { Rows.Count: > 0 } list)
        {
            var row = list.Rows[0];
            for (var i = 0; i < list.Columns.Count && i < row.Count; i++) values.Outputs[prefix + "row." + list.Columns[i]] = row[i];
        }
        return Outcome.Ok();
    }

    private async Task<Outcome> RunRestAsync(Workflow workflow, WorkflowStep step, WorkflowValues values, CancellationToken ct)
    {
        var config = step.Rest;
        if (config is null) return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);

        // values land in the path / query, so they are URL-encoded; the scheme and host are written out in the step
        var url = WorkflowTemplate.Expand(config.Url.Trim(), values.Get, Uri.EscapeDataString);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            logger.LogWarning("Workflow {WorkflowCode} step {Step}: the URL is not a valid http(s) address.", workflow.Code, step.Code);
            return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);
        }

        var headers = config.Headers
            .Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .Select(h => KeyValuePair.Create(h.Name.Trim(), StripLineBreaks(WorkflowTemplate.Expand(h.Value, values.Get))))
            .ToList();

        var contentType = string.IsNullOrWhiteSpace(config.ContentType) ? "application/json" : config.ContentType.Trim();
        var body = string.IsNullOrEmpty(config.Body)
            ? null
            : WorkflowTemplate.Expand(config.Body, values.Get, contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ? WorkflowTemplate.JsonEscape : null);

        RestResponse response;
        try
        {
            response = await rest.SendAsync(new RestRequest(
                config.Method.Trim().ToUpperInvariant(), uri, headers, body, contentType, config.TimeoutSeconds, config.Retries), ct);
        }
        catch (RestHostNotAllowedException ex)
        {
            logger.LogWarning("Workflow {WorkflowCode} step {Step}: {Reason}", workflow.Code, step.Code, ex.Message);
            return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);
        }
        catch (RestCallException ex)
        {
            return Outcome.Fail(ex.IsTimeout ? WorkflowFailureKind.Timeout : WorkflowFailureKind.Error, ex.Message);
        }

        values.Outputs[step.Code + ".status"] = response.Status;
        values.Outputs[step.Code + ".body"] = response.Body;
        return response.Status is >= 200 and < 300
            ? Outcome.Ok()
            : Outcome.Fail(WorkflowFailureKind.Business, $"Layanan eksternal menjawab HTTP {response.Status}.");
    }

    private async Task<Outcome> RunEmailAsync(Workflow workflow, WorkflowStep step, WorkflowValues values, CancellationToken ct)
    {
        var config = step.Email;
        if (config is null) return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);

        var recipients = WorkflowTemplate.Expand(config.To, values.Get)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (recipients.Count == 0)
            return Outcome.Fail(WorkflowFailureKind.Business, "Penerima email kosong.");
        if (recipients.Count > EmailStepConfig.MaxRecipients)
            return Outcome.Fail(WorkflowFailureKind.Business, $"Penerima email maksimal {EmailStepConfig.MaxRecipients}.");
        foreach (var address in recipients)
            if (!MailAddress.TryCreate(address, out var parsed) || !string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase))
                return Outcome.Fail(WorkflowFailureKind.Business, $"Alamat email tidak valid: {address}");

        var subject = StripLineBreaks(WorkflowTemplate.Expand(config.Subject, values.Get)).Trim();
        if (subject.Length > EmailStepConfig.MaxSubjectLength) subject = subject[..EmailStepConfig.MaxSubjectLength];
        var body = config.IsHtml
            ? ModulePageHtml.Sanitize(WorkflowTemplate.Expand(config.Body, values.Get, WebUtility.HtmlEncode))
            : WorkflowTemplate.Expand(config.Body, values.Get);

        if (!email.IsConfigured)
        {
            logger.LogInformation("Workflow {WorkflowCode} step {Step}: email to {Count} recipient(s) with subject '{Subject}' was NOT sent (Rakit:Mail is not configured).",
                workflow.Code, step.Code, recipients.Count, subject);
            values.Outputs[step.Code + ".sent"] = "false";
            return Outcome.Ok("Email tidak dikirim: SMTP belum dikonfigurasi (hanya dicatat).");
        }

        try
        {
            await email.SendAsync(new EmailMessage(recipients, subject, body, config.IsHtml), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Workflow {WorkflowCode} step {Step}: the email could not be sent.", workflow.Code, step.Code);
            return Outcome.Fail(WorkflowFailureKind.Error, "Email gagal dikirim.");
        }
        values.Outputs[step.Code + ".sent"] = "true";
        return Outcome.Ok();
    }

    private static Outcome RunPage(WorkflowStep step, WorkflowValues values)
    {
        var config = step.Page;
        if (config is null) return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);
        // values are HTML-encoded first and the whole result is sanitized again
        var html = ModulePageHtml.Sanitize(WorkflowTemplate.Expand(config.Html, values.Get, WebUtility.HtmlEncode));
        return Outcome.Ok(display: new WorkflowDisplay("html", html, null));
    }

    private async Task<Outcome> RunModuleAsync(Workflow workflow, WorkflowStep step, WorkflowValues values, CancellationToken ct)
    {
        var config = step.Module;
        if (config is null) return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);

        var path = await moduleLinks.PathOfAsync(config.ModuleId, ct);
        if (path is null)
        {
            logger.LogWarning("Workflow {WorkflowCode} step {Step}: module {ModuleId} is gone or not published.", workflow.Code, step.Code, config.ModuleId);
            return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);
        }

        var query = string.Join("&", config.Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => Uri.EscapeDataString(p.Name.Trim()) + "=" + Uri.EscapeDataString(WorkflowTemplate.Expand(p.Value, values.Get))));
        return Outcome.Ok(display: new WorkflowDisplay("module", null, query.Length == 0 ? path : path + "?" + query));
    }

    private Outcome RunScript(Workflow workflow, WorkflowStep step, WorkflowValues values)
    {
        var config = step.Script;
        if (config is null) return Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable);

        var result = scripts.Run(config.Source, WorkflowScriptContext.ToJson(values));
        switch (result.Failure)
        {
            case ScriptFailureKind.None:
                if (result.Json is not null) values.Outputs[step.Code + ".body"] = result.Json;
                return Outcome.Ok();
            case ScriptFailureKind.Thrown:
                return Outcome.Fail(WorkflowFailureKind.Business, result.Message ?? JintScriptEngine.GenericError);
            default:
                logger.LogWarning("Workflow {WorkflowCode} step {Step}: the script failed ({Kind}): {Detail}", workflow.Code, step.Code, result.Failure, result.Detail);
                return Outcome.Fail(
                    result.Failure == ScriptFailureKind.Limit ? WorkflowFailureKind.Timeout : WorkflowFailureKind.Error,
                    result.Message ?? JintScriptEngine.GenericError);
        }
    }

    private static Outcome RunCondition(WorkflowStep step, WorkflowValues values)
        => step.Condition is null
            ? Outcome.Fail(WorkflowFailureKind.Unavailable, GenericUnavailable)
            : Outcome.Ok(condition: WorkflowCondition.Evaluate(step.Condition, values.Get));

    private static string StripLineBreaks(string text) => text.Replace("\r", "").Replace("\n", " ");
}
