using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rakit.Core.DataSources;

/// <summary>JSON and column-value conversions for the rakit.DataSources tables.</summary>
public static class DataSourceJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(DataSourceDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static DataSourceDefinition Deserialize(string json)
        => Repair(JsonSerializer.Deserialize<DataSourceDefinition>(json, Options) ?? new DataSourceDefinition());

    /// <summary>
    /// JSON may say null where the model has a list ("parameters": null, a null filter); the validator, the table query
    /// builder and the resolver count on the lists being there, so a null list becomes empty and null items are dropped.
    /// </summary>
    private static DataSourceDefinition Repair(DataSourceDefinition definition)
    {
        definition.Parameters = definition.Parameters?.OfType<ParameterMapping>().ToList() ?? [];
        if (definition.Table is { } table)
        {
            table.Columns = table.Columns?.OfType<string>().ToList() ?? [];
            table.Filters = table.Filters?.OfType<TableFilter>().ToList() ?? [];
            table.Sort = table.Sort?.OfType<TableSort>().ToList() ?? [];
        }
        return definition;
    }

    public static string ModeToColumn(DataSourceMode mode) => mode switch
    {
        DataSourceMode.Table => "table",
        DataSourceMode.Query => "query",
        DataSourceMode.StoredProcedure => "sp",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static DataSourceMode ModeFromColumn(string value) => value switch
    {
        "table" => DataSourceMode.Table,
        "query" => DataSourceMode.Query,
        "sp" => DataSourceMode.StoredProcedure,
        _ => throw new InvalidOperationException($"Unknown data source mode '{value}'."),
    };

    public static string StatementToColumn(StatementType type) => type == StatementType.Command ? "command" : "query";

    public static StatementType StatementFromColumn(string value) => value switch
    {
        "query" => StatementType.Query,
        "command" => StatementType.Command,
        _ => throw new InvalidOperationException($"Unknown statement type '{value}'."),
    };
}
