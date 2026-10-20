using Rakit.Core.DataSources;

namespace Rakit.Core.Sql;

public sealed record SqlExecOptions
{
    public const int DefaultMaxRows = 1000;

    public int TimeoutSeconds { get; init; } = DataSourceDefinition.DefaultTimeoutSeconds;

    /// <summary>Rows kept per result set; the rest is not read. Null = unlimited.</summary>
    public int? MaxRows { get; init; } = DefaultMaxRows;

    /// <summary><see cref="StatementType.Query"/> returns rows; <see cref="StatementType.Command"/> returns rows affected.</summary>
    public StatementType StatementType { get; init; } = StatementType.Query;

    /// <summary>
    /// Procedures only: run inside a transaction that is always rolled back (builder preview), so a procedure that
    /// writes leaves nothing behind. Not honoured by <see cref="ISqlExecutionService.ExecuteQueryAsync"/>.
    /// </summary>
    public bool RollbackAfter { get; init; }

    /// <summary>
    /// Procedures only: run inside a transaction that is committed only when the procedure finishes without error and
    /// returns 0 (the convention: 0 = success, anything else = a business error code); any other return value rolls it back.
    /// Ignored when <see cref="RollbackAfter"/> is set. Not honoured by <see cref="ISqlExecutionService.ExecuteQueryAsync"/>.
    /// </summary>
    public bool CommitOnlyIfReturnValueZero { get; init; }
}

public sealed record DataTableResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated);

public sealed record SqlExecResult(
    IReadOnlyList<DataTableResult> ResultSets,
    IDictionary<string, object?> OutputParameters,
    int? ReturnValue,
    int RowsAffected,
    TimeSpan Duration);

public enum SqlExecErrorKind
{
    /// <summary>Raised on purpose by the SQL (THROW / RAISERROR, error number 50000+): safe to show to the end user.</summary>
    Business,
    Timeout,
    /// <summary>Anything else (syntax, permissions, missing object, ...). Logged; end users get a generic message.</summary>
    Database,
}

public sealed class SqlExecutionException(SqlExecErrorKind kind, string message, string? detail = null, Exception? inner = null)
    : Exception(message, inner)
{
    public SqlExecErrorKind Kind { get; } = kind;

    /// <summary>The database's own message. For builder screens only; never show it to end users.</summary>
    public string? Detail { get; } = detail;
}
