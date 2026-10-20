using System.Text.Json.Serialization;

namespace Rakit.Core.DataSources;

public enum DataSourceMode { Table, Query, StoredProcedure }

public enum StatementType { Query, Command }

public enum ParameterSource { QueryString, FormField, RouteValue, UserClaim, Constant, StepOutput, Expression }

/// <summary>Where a parameter value comes from. <c>Key</c> picks the entry in the source; <c>Value</c> is the constant/expression.</summary>
public sealed class ParameterMapping
{
    public string Name { get; set; } = "";
    public ParameterSource Source { get; set; } = ParameterSource.Constant;
    public string? Key { get; set; }
    public string? Value { get; set; }
}

public sealed class TableFilter
{
    public string Column { get; set; } = "";
    /// <summary>One of =, &lt;&gt;, &gt;, &gt;=, &lt;, &lt;=, like.</summary>
    public string Operator { get; set; } = "=";
    /// <summary>Name of a mapped parameter that supplies the value.</summary>
    public string Parameter { get; set; } = "";
}

public sealed class TableSort
{
    public string Column { get; set; } = "";
    public bool Descending { get; set; }
}

public sealed class TableDefinition
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = [];
    public List<TableFilter> Filters { get; set; } = [];
    public List<TableSort> Sort { get; set; } = [];
}

/// <summary>The stored procedure a data source calls. Must be whitelisted (rakit.SpWhitelist) to be saved or run.</summary>
public sealed class ProcedureReference
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Stored as JSON in rakit.DataSources.Definition.</summary>
public sealed class DataSourceDefinition
{
    public const int DefaultTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Highest result set index a stored procedure data source can point at.</summary>
    public const int MaxResultSetIndex = 9;

    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
    public TableDefinition? Table { get; set; }
    public string? Query { get; set; }
    public ProcedureReference? Procedure { get; set; }

    /// <summary>Stored procedure mode: zero-based index of the result set that holds the rows.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ListResultSet { get; set; }

    /// <summary>
    /// Stored procedure mode: result set that holds the total row count. Null = ask the procedure again with
    /// <c>@IsCount = 1</c> when it declares that parameter.
    /// </summary>
    public int? CountResultSet { get; set; }

    public List<ParameterMapping> Parameters { get; set; } = [];

    /// <summary>Drops the parts that do not belong to <paramref name="mode"/>; only the active mode's part is stored.</summary>
    public void KeepOnlyPartFor(DataSourceMode mode)
    {
        if (mode != DataSourceMode.Table) Table = null;
        if (mode != DataSourceMode.Query) Query = null;
        if (mode != DataSourceMode.StoredProcedure)
        {
            Procedure = null;
            ListResultSet = 0;
            CountResultSet = null;
        }
    }
}

/// <summary>What a caller supplies to create or update a data source.</summary>
public sealed class DataSourceInput
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DataSourceMode Mode { get; set; } = DataSourceMode.Query;
    public StatementType StatementType { get; set; } = StatementType.Query;
    public DataSourceDefinition Definition { get; set; } = new();
}

public sealed record DataSource(
    Guid Id,
    string Code,
    string Name,
    DataSourceMode Mode,
    StatementType StatementType,
    int Version,
    DataSourceDefinition Definition,
    DateTime Created,
    DateTime? Updated);

public sealed record DataSourceSummary(
    Guid Id, string Code, string Name, DataSourceMode Mode, StatementType StatementType, int Version, DateTime? Updated);

public sealed record DataSourceVersion(
    int Version,
    string Name,
    DataSourceMode Mode,
    StatementType StatementType,
    DataSourceDefinition Definition,
    string? ChangedBy,
    DateTime Changed);
