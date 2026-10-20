using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rakit.Core.Workflows;

/// <summary>JSON and column-value conversions for the rakit.Workflows tables.</summary>
public static class WorkflowJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SerializeStep(WorkflowStep step) => JsonSerializer.Serialize(step, Options);

    public static WorkflowStep DeserializeStep(string json)
        => JsonSerializer.Deserialize<WorkflowStep>(json, Options) ?? new WorkflowStep();

    /// <summary>The step list a builder form posts as one JSON field. Throws <see cref="JsonException"/> on unreadable text.</summary>
    public static List<WorkflowStep> DeserializeSteps(string? json)
        => string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<WorkflowStep>>(json, Options) ?? [];

    public static string SerializeSteps(IEnumerable<WorkflowStep> steps) => JsonSerializer.Serialize(steps, Options);

    public static string SerializeData(object data) => JsonSerializer.Serialize(data, Options);

    public static T? DeserializeData<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string StatusToColumn(WorkflowStatus status) => status == WorkflowStatus.Published ? "published" : "draft";

    public static WorkflowStatus StatusFromColumn(string value) => value switch
    {
        "draft" => WorkflowStatus.Draft,
        "published" => WorkflowStatus.Published,
        _ => throw new InvalidOperationException($"Unknown workflow status '{value}'."),
    };

    public static string TrackingToText(TrackingStatus status) => status switch
    {
        TrackingStatus.Success => "success",
        TrackingStatus.Failed => "failed",
        TrackingStatus.RolledBack => "rolledback",
        _ => "skipped",
    };

    public static TrackingStatus TrackingFromText(string? value) => value switch
    {
        "success" => TrackingStatus.Success,
        "failed" => TrackingStatus.Failed,
        "rolledback" => TrackingStatus.RolledBack,
        _ => TrackingStatus.Skipped,
    };

    /// <summary>The workflow-wide options stored in rakit.Workflows.Property.</summary>
    public sealed class Settings
    {
        public bool Transaction { get; set; }
    }

    public static string SerializeSettings(Settings settings) => JsonSerializer.Serialize(settings, Options);

    public static Settings DeserializeSettings(string? json)
        => string.IsNullOrWhiteSpace(json) ? new Settings() : JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
}
