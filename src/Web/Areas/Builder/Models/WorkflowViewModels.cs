using Rakit.Core.Workflows;

namespace Rakit.Web.Areas.Builder.Models;

public sealed record WorkflowIndexViewModel(IReadOnlyList<WorkflowSummary> Items, string? Message, string? Error = null);

/// <summary>A data source a workflow step can run (Query or Command), already labelled for the dropdown.</summary>
public sealed record StepSourceOption(Guid Id, string Label);

/// <summary>One row of the "Riwayat" table: a tracking entry with the time already local.</summary>
public sealed record TrackingRow(
    DateTime Time, Guid RunId, string StepCode, string? StepName, TrackingStatus Status, string? Message, int DurationMs, string? ActionCode);

/// <summary>
/// Posted fields: Id, Code, Name, Description, StepsJson. The step list is edited in the browser and travels as one JSON field
/// (an array of <c>{code, name, type, dataSourceId}</c>).
/// </summary>
public sealed class WorkflowFormViewModel
{
    public Guid? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string StepsJson { get; set; } = "[]";

    // Display only.
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Draft;
    public IReadOnlyList<StepSourceOption> DataSources { get; set; } = [];
    public IReadOnlyList<TrackingRow> History { get; set; } = [];

    public bool IsEdit => Id is not null;
}

public static class WorkflowDisplayText
{
    public static string StatusLabel(WorkflowStatus status) => status == WorkflowStatus.Published ? "Published" : "Draft";

    public static string TrackingLabel(TrackingStatus status) => status switch
    {
        TrackingStatus.Success => "Berhasil",
        TrackingStatus.Failed => "Gagal",
        _ => "Dilewati",
    };
}
