namespace Rakit.Core.DataSources;

/// <summary>
/// The request/workflow facts a parameter mapping can read from. Lookups are case-insensitive.
/// Build it from the current HTTP request (runtime pages) or from a workflow run (StepOutputs).
/// </summary>
public sealed class ParameterContext
{
    private static readonly IReadOnlyDictionary<string, string?> NoStrings = new Dictionary<string, string?>();
    private static readonly IReadOnlyDictionary<string, object?> NoObjects = new Dictionary<string, object?>();

    public IReadOnlyDictionary<string, string?> QueryString { get; init; } = NoStrings;
    public IReadOnlyDictionary<string, string?> Form { get; init; } = NoStrings;
    public IReadOnlyDictionary<string, string?> Route { get; init; } = NoStrings;

    /// <summary>Claim type to value (first value when a claim repeats).</summary>
    public IReadOnlyDictionary<string, string?> Claims { get; init; } = NoStrings;

    /// <summary>Earlier workflow step outputs, keyed <c>stepCode.outputName</c>.</summary>
    public IReadOnlyDictionary<string, object?> StepOutputs { get; init; } = NoObjects;

    /// <summary>Clock used by expressions; injectable so results are reproducible.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}
