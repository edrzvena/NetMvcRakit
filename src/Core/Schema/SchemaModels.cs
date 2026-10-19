namespace Rakit.Core.Schema;

/// <summary>Kind is "TABLE" or "VIEW".</summary>
public sealed record TableInfo(string Schema, string Name, string Kind);

public sealed record ColumnInfo(string Name, string DataType, int MaxLength, byte Precision, byte Scale, bool IsNullable);

public sealed record ProcedureInfo(string Schema, string Name);

public sealed record ProcedureParameter(
    string Name, string DataType, int MaxLength, byte Precision, byte Scale, bool IsOutput, bool HasDefault);

public sealed record ResultColumn(string Name, string DataType, bool IsNullable);

public sealed record ProcedureDetail(
    string Schema,
    string Name,
    IReadOnlyList<ProcedureParameter> Parameters,
    IReadOnlyList<ResultColumn> ResultColumns,
    string? ResultSetWarning);
