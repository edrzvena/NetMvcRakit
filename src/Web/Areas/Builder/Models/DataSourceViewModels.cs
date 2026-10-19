using System.Text.Json;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;

namespace Rakit.Web.Areas.Builder.Models;

public sealed record DataSourceIndexViewModel(
    IReadOnlyList<DataSourceSummary> Items, bool AllowRawQuery, bool AllowWrite, string? Message);

/// <summary>
/// Posted fields: Id, Code, Name, Mode, StatementType, DefinitionJson.
/// The dynamic parts (columns, filters, sort, parameters) are edited in the browser and travel as one JSON field
/// shaped exactly like the stored definition.
/// </summary>
public sealed class DataSourceFormViewModel
{
    public Guid? Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public DataSourceMode Mode { get; set; } = DataSourceMode.Table;
    public StatementType StatementType { get; set; } = StatementType.Query;
    public string DefinitionJson { get; set; } = "{}";

    // Display only (never posted back meaningfully).
    public int? Version { get; set; }
    public IReadOnlyList<TableInfo> Tables { get; set; } = [];
    public bool TablesUnavailable { get; set; }
    public bool AllowRawQuery { get; set; }
    public bool AllowWrite { get; set; }
    public bool IsEdit => Id is not null;
}

public sealed record DataSourceVersionsViewModel(DataSource Current, IReadOnlyList<DataSourceVersion> Versions);

public static class DataSourceDisplay
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string PrettyJson(DataSourceDefinition definition)
    {
        using var doc = JsonDocument.Parse(DataSourceJson.Serialize(definition));
        return JsonSerializer.Serialize(doc.RootElement, Indented);
    }

    public static string ModeLabel(DataSourceMode mode) => mode switch
    {
        DataSourceMode.Table => "Table",
        DataSourceMode.Query => "Query",
        _ => "Stored Procedure",
    };
}
