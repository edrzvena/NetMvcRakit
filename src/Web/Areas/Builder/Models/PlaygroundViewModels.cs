using Rakit.Core.Playground;

namespace Rakit.Web.Areas.Builder.Models;

public sealed record PlaygroundIndexViewModel(PlaygroundSettings Settings, IReadOnlyList<string> ColumnTypes);

/// <summary>Body of POST /Builder/Playground/Script. Kind: select, insert, update, delete, exec, create, definition, alter, addColumn, drop.</summary>
public sealed class PlaygroundScriptBody
{
    public string Kind { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Body of POST /Builder/Playground/Run.</summary>
public sealed class PlaygroundRunBody
{
    public string? Sql { get; set; }
    public int? MaxRows { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool Rollback { get; set; }
}

public sealed record PlaygroundRunResponse(
    bool Success,
    IReadOnlyList<PlaygroundResultSet> ResultSets,
    IReadOnlyList<PlaygroundMessage> Messages,
    PlaygroundError? Error,
    long DurationMs,
    bool Committed,
    bool Write);
