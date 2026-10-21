using System.Text.Json.Serialization;
using Rakit.Core.Modules;

namespace Rakit.Core.Workflows;

public enum WorkflowStatus { Draft, Published }

/// <summary>
/// Serialised camelCase. <c>DataSource</c> covers the PRD's Database and StoredProcedure steps (it runs a saved data source);
/// <c>Page</c> and <c>Module</c> are the PRD's Html/Page and Module/Widget steps: they do not run anything, they tell the
/// visitor's browser what to show next (a message page, or another module).
/// </summary>
public enum StepType { DataSource, Rest, Email, Page, Module, Condition, Javascript, Action }

/// <summary>Serialised camelCase: eq, ne, gt, ge, lt, le, contains, empty, notEmpty.</summary>
public enum ConditionOperator { Eq, Ne, Gt, Ge, Lt, Le, Contains, Empty, NotEmpty }

public sealed class NameValue
{
    public string Name { get; set; } = "";

    /// <summary>May hold <c>{{key}}</c> placeholders (see <see cref="WorkflowTemplate"/>).</summary>
    public string Value { get; set; } = "";
}

/// <summary>
/// A call to an external HTTP service. The URL's scheme and host must be written out (no placeholders there) and the host has
/// to be in <c>Rakit:Workflow:AllowedHosts</c>; placeholders are welcome in the path, query, headers and body. Outputs for
/// later steps: <c>{code}.status</c>, <c>{code}.body</c> and <c>{code}.json.{path}</c> (a dotted path into a JSON response,
/// e.g. <c>items.0.id</c>).
/// </summary>
public sealed class RestStepConfig
{
    public const int MaxUrlLength = 2000;
    public const int MaxBodyLength = 20_000;
    public const int MaxHeaders = 10;
    public const int MaxHeaderValueLength = 1000;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 60;
    public const int MaxRetries = 3;

    public static readonly IReadOnlyList<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<NameValue> Headers { get; set; } = [];

    /// <summary>
    /// Request body (POST/PUT/PATCH/DELETE). Placeholder values are escaped for the content type: JSON-escaped (JSON),
    /// percent-encoded (form-urlencoded), XML-escaped (XML); plain text gets them as they are (see <see cref="WorkflowTemplate.BodyEncoder"/>).
    /// </summary>
    public string? Body { get; set; }

    /// <summary>Content type of the body; blank = <c>application/json</c>.</summary>
    public string? ContentType { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Extra attempts after a network error, a timeout, 408, 429 or a 5xx answer (0..3).</summary>
    public int Retries { get; set; }
}

/// <summary>An email. Outputs: <c>{code}.sent</c> = <c>true</c>, or <c>false</c> when SMTP is not configured (the message is only logged).</summary>
public sealed class EmailStepConfig
{
    public const int MaxRecipients = 10;
    public const int MaxFieldLength = 500;
    public const int MaxSubjectLength = 200;
    public const int MaxBodyLength = 20_000;

    /// <summary>Addresses separated by comma or semicolon; may hold placeholders (e.g. <c>{{form.Email}}</c>).</summary>
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    /// <summary>The body is HTML (placeholder values are HTML-encoded and the result is sanitized).</summary>
    public bool IsHtml { get; set; }
}

/// <summary>Shows a message to the visitor once the workflow is done (sanitized HTML; placeholder values are HTML-encoded).</summary>
public sealed class PageStepConfig
{
    public const int MaxHtmlLength = 20_000;
    public string Html { get; set; } = "";
}

/// <summary>Sends the visitor on to another module, with query-string parameters (a data source can read them as QueryString).</summary>
public sealed class ModuleStepConfig
{
    public const int MaxParameters = 10;
    public const int MaxValueLength = 500;

    public Guid ModuleId { get; set; }
    public List<NameValue> Parameters { get; set; } = [];
}

/// <summary>
/// Chooses the next step. <see cref="Left"/> and <see cref="Right"/> are text with placeholders; two numbers compare as numbers
/// (invariant culture), anything else as text (case-insensitive). A branch without a step ends the workflow.
/// </summary>
public sealed class ConditionStepConfig
{
    public const int MaxOperandLength = 500;

    public string Left { get; set; } = "";
    public ConditionOperator Operator { get; set; } = ConditionOperator.Eq;
    public string? Right { get; set; }
    public string? OnTrue { get; set; }
    public string? OnFalse { get; set; }

    public static bool NeedsRight(ConditionOperator op) => op is not (ConditionOperator.Empty or ConditionOperator.NotEmpty);
}

/// <summary>
/// A script run on the server in a sandbox (no .NET, file or network access; time, memory and statement limits). The source is the
/// body of a function with one argument, <c>ctx</c> = <c>{ form, key, claims, now, steps }</c> (<c>steps.code.row.Column</c> ... the
/// outputs of earlier steps), and <c>return</c>s a value that can be written as JSON. That JSON is the step's output: <c>{code}.body</c>,
/// read by later steps as <c>{{code.json.path}}</c>. <c>throw new Error("text")</c> fails the step with that text for the user.
/// </summary>
public sealed class ScriptStepConfig
{
    public const int MaxLength = 20_000;

    public string Source { get; set; } = "";
}

/// <summary>A button of an Action step. A <see cref="Cancel"/> button ends the workflow without running the steps after the Action.</summary>
public sealed class ActionButton
{
    public const int MaxCodeLength = 30;
    public const int MaxLabelLength = 50;

    public string Code { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>One of <see cref="ModuleAction.Styles"/>; blank = primary.</summary>
    public string? Style { get; set; }

    public bool Cancel { get; set; }
}

/// <summary>
/// Stops the run and shows the visitor a form; the run goes on when a button is pressed. Outputs for later steps:
/// <c>{code}.button</c> (the button's code) and <c>{code}.form.FieldName</c> (the value typed, as text). The fields are validated
/// with the same rules as a module form; a select needs fixed options (no lookup) and a browse field is not available.
/// </summary>
public sealed class ActionStepConfig
{
    public const int MaxFields = 20;
    public const int MaxButtons = 4;
    public const int MaxTitleLength = 100;
    public const int MaxIntroLength = 500;

    /// <summary>How long the visitor has to answer before the run is no longer valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public string Title { get; set; } = "";

    /// <summary>Plain text under the title.</summary>
    public string? Intro { get; set; }

    public List<ModuleFormField> Fields { get; set; } = [];
    public List<ActionButton> Buttons { get; set; } = [];
}

/// <summary>
/// One step of a workflow. Steps form a graph: <see cref="Next"/> names the step that follows (null = the workflow ends here);
/// a Condition step names its two branches instead. The first step in the list is where the run starts.
/// </summary>
public sealed class WorkflowStep
{
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 100;

    /// <summary>Identifier used in placeholders and in tracking: letters, digits and underscores, starting with a letter.</summary>
    public string Code { get; set; } = "";

    /// <summary>Shown to the user in messages and tracking; blank = the code.</summary>
    public string? Name { get; set; }

    public StepType Type { get; set; } = StepType.DataSource;

    /// <summary>The step that runs after this one (not used by a Condition); null = end.</summary>
    public string? Next { get; set; }

    /// <summary>Canvas position (the builder's layout only).</summary>
    public int? X { get; set; }
    public int? Y { get; set; }

    /// <summary>DataSource steps: the data source to run (Query or Command).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid DataSourceId { get; set; }

    public RestStepConfig? Rest { get; set; }
    public EmailStepConfig? Email { get; set; }
    public PageStepConfig? Page { get; set; }
    public ModuleStepConfig? Module { get; set; }
    public ConditionStepConfig? Condition { get; set; }
    public ScriptStepConfig? Script { get; set; }
    public ActionStepConfig? Action { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Code : Name.Trim();
}

/// <summary>What a caller supplies to create or update a workflow. Status is changed separately; the first step is the entry point.</summary>
public sealed class WorkflowInput
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// Run all DataSource steps on one connection and one SQL transaction: if any step fails, every change they made is rolled
    /// back. Rest, Email and Page/Module steps are not part of it (they cannot be undone).
    /// </summary>
    public bool Transaction { get; set; }

    public List<WorkflowStep> Steps { get; set; } = [];
}

public sealed record Workflow(
    Guid Id, string Code, string Name, string? Description, WorkflowStatus Status, bool Transaction,
    IReadOnlyList<WorkflowStep> Steps, DateTime Created, DateTime? Updated);

public sealed record WorkflowSummary(
    Guid Id, string Code, string Name, WorkflowStatus Status, int StepCount, DateTime? Updated);

public enum TrackingStatus
{
    Success,
    Failed,

    /// <summary>Not run because an earlier step failed.</summary>
    Skipped,

    /// <summary>The step succeeded but the workflow's transaction was rolled back because a later step failed.</summary>
    RolledBack,

    /// <summary>An Action step is showing its form; the run continues when the visitor presses a button (a later row for the same step says how it went).</summary>
    Waiting,
}

/// <summary>One executed (or skipped) step of a run. <see cref="RunId"/> groups the steps of one run.</summary>
public sealed record TrackingEntry(
    Guid Id, Guid RunId, Guid WorkflowId, string StepCode, string? StepName, TrackingStatus Status,
    string? Message, int DurationMs, Guid? ModuleId, string? ActionCode, string? RunBy, DateTime Created);

public sealed class WorkflowNotFoundException(Guid id) : Exception($"Workflow '{id}' not found.");

/// <summary>No waiting run with that id (never existed, other module, expired, already answered, or its workflow is gone or a draft).</summary>
public sealed class WorkflowRunNotFoundException(Guid runId) : Exception($"Workflow run '{runId}' is not waiting for an answer.");

/// <summary>The input failed validation; <see cref="Errors"/> are safe to show to the builder.</summary>
public sealed class WorkflowValidationException(IReadOnlyList<string> errors)
    : Exception("Workflow input is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class DuplicateWorkflowCodeException(string code) : Exception($"Workflow code '{code}' is already used.")
{
    public string Code { get; } = code;
}

/// <summary>Modules still run the workflow from one of their actions.</summary>
public sealed class WorkflowInUseException(Guid id, int moduleCount)
    : Exception($"Workflow '{id}' is still used by {moduleCount} module(s).")
{
    public int ModuleCount { get; } = moduleCount;
}
