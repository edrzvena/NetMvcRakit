namespace Rakit.Core.Workflows;

public enum WorkflowStatus { Draft, Published }

/// <summary>Serialised camelCase. Only <see cref="DataSource"/> exists so far (it covers the PRD's Database and StoredProcedure steps).</summary>
public enum StepType { DataSource }

/// <summary>
/// One step of a workflow: it runs a data source (Query or Command; table, query or stored procedure). The data source's own
/// parameter mappings decide where its values come from; the ones that read <c>StepOutput</c> use the keys
/// <c>{code}.return</c>, <c>{code}.out.{Parameter}</c> and <c>{code}.row.{Column}</c> of an earlier step.
/// </summary>
public sealed class WorkflowStep
{
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 100;

    /// <summary>Identifier used in StepOutput keys and in tracking: letters, digits and underscores, starting with a letter.</summary>
    public string Code { get; set; } = "";

    /// <summary>Shown to the user in messages and tracking; blank = the code.</summary>
    public string? Name { get; set; }

    public StepType Type { get; set; } = StepType.DataSource;
    public Guid DataSourceId { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Code : Name.Trim();
}

/// <summary>What a caller supplies to create or update a workflow. Status is changed separately; steps run in list order.</summary>
public sealed class WorkflowInput
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<WorkflowStep> Steps { get; set; } = [];
}

public sealed record Workflow(
    Guid Id, string Code, string Name, string? Description, WorkflowStatus Status,
    IReadOnlyList<WorkflowStep> Steps, DateTime Created, DateTime? Updated);

public sealed record WorkflowSummary(
    Guid Id, string Code, string Name, WorkflowStatus Status, int StepCount, DateTime? Updated);

public enum TrackingStatus { Success, Failed, Skipped }

/// <summary>One executed (or skipped) step of a run. <see cref="RunId"/> groups the steps of one run.</summary>
public sealed record TrackingEntry(
    Guid Id, Guid RunId, Guid WorkflowId, string StepCode, string? StepName, TrackingStatus Status,
    string? Message, int DurationMs, Guid? ModuleId, string? ActionCode, string? RunBy, DateTime Created);

public sealed class WorkflowNotFoundException(Guid id) : Exception($"Workflow '{id}' not found.");

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
