using Rakit.Core.Schema;

namespace Rakit.Core.Playground;

/// <summary>What the screen needs to know before it offers anything.</summary>
public sealed record PlaygroundSettings(
    bool Enabled, bool AllowWrite, int DefaultMaxRows, int MaxRowsLimit, int DefaultTimeoutSeconds, int MaxTimeoutSeconds,
    IReadOnlyList<string> Schemas);

/// <summary>Kind is TABLE, VIEW, PROCEDURE or FUNCTION.</summary>
public sealed record PlaygroundObject(string Schema, string Name, string Kind);

public sealed record PlaygroundTreeSchema(string Name, IReadOnlyList<PlaygroundObject> Objects);

public sealed record PlaygroundColumn(
    int Ordinal, string Name, string Type, bool IsNullable, bool IsIdentity, string? IdentityText,
    string? DefaultDefinition, string? ComputedDefinition, string? Collation, bool IsPrimaryKey);

public sealed record PlaygroundIndexColumn(string Name, bool Descending);

public sealed record PlaygroundIndex(
    string Name, string Type, bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint,
    IReadOnlyList<PlaygroundIndexColumn> KeyColumns, IReadOnlyList<string> IncludedColumns, string? Filter);

/// <summary>OnDelete / OnUpdate are the catalog's action names: NO_ACTION, CASCADE, SET_NULL, SET_DEFAULT.</summary>
public sealed record PlaygroundForeignKey(
    string Name, IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns, string OnDelete, string OnUpdate);

public sealed record PlaygroundCheck(string Name, string Definition);

public sealed record PlaygroundObjectDetail(
    string Schema, string Name, string Kind, long? ApproxRows,
    IReadOnlyList<PlaygroundColumn> Columns,
    IReadOnlyList<PlaygroundIndex> Indexes,
    IReadOnlyList<PlaygroundForeignKey> ForeignKeys,
    IReadOnlyList<PlaygroundCheck> Checks,
    IReadOnlyList<ProcedureParameter> Parameters,
    string? Definition);

// ---- running ---------------------------------------------------------------------------------

/// <param name="Rollback">Write mode only: run the script and roll it back (a dry run). Read-only runs always roll back.</param>
public sealed record PlaygroundRunRequest(string Sql, int? MaxRows, int? TimeoutSeconds, bool Rollback);

public sealed record PlaygroundColumnInfo(string Name, string Type);

/// <summary>Rows beyond the cap are read and dropped; <see cref="TotalRows"/> counts all of them.</summary>
public sealed record PlaygroundResultSet(
    int Batch, IReadOnlyList<PlaygroundColumnInfo> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, long TotalRows)
{
    public bool Truncated => TotalRows > Rows.Count;
}

public sealed record PlaygroundMessage(int Batch, string Text);

/// <param name="Line">Line in the whole script (null inside a procedure, where <see cref="Procedure"/> names it).</param>
public sealed record PlaygroundError(
    string Message, int? Line, int Number, int Severity, int Batch, string? Procedure, int? ProcedureLine, string? Hint);

public sealed record PlaygroundRunResult(
    bool Success,
    IReadOnlyList<PlaygroundResultSet> ResultSets,
    IReadOnlyList<PlaygroundMessage> Messages,
    PlaygroundError? Error,
    TimeSpan Duration,
    bool Committed,
    bool Write);

// ---- table designer --------------------------------------------------------------------------

public sealed class NewTableColumn
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>Character / byte length for (n)varchar, (n)char, (var)binary: a number or "max".</summary>
    public string? Length { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool Nullable { get; set; } = true;
    public bool Identity { get; set; }
    public bool PrimaryKey { get; set; }
    public string? Default { get; set; }
}

public sealed class NewTableSpec
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public List<NewTableColumn> Columns { get; set; } = [];
}

// ---- errors ----------------------------------------------------------------------------------

/// <summary>Rakit:Playground:Enabled is false.</summary>
public sealed class PlaygroundDisabledException() : Exception("Playground dimatikan (Rakit:Playground:Enabled = false).");
