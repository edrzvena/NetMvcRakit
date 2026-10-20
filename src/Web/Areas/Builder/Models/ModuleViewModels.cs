using System.Text.Json;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;

namespace Rakit.Web.Areas.Builder.Models;

public sealed record ModuleIndexViewModel(IReadOnlyList<ModuleSummary> Items, string? Message, string? Error = null);

/// <summary>A data source a Table module can list from (statement type Query only).</summary>
public sealed record DataSourceOption(Guid Id, string Code, string Name, string ModeLabel);

/// <summary>
/// Posted fields: Id, Name, Description, GroupId, NewGroupName, DataSourceId, ConfigJson.
/// The Display part (and the stored access setting) is edited in the browser and travels as one JSON field shaped like
/// the stored config; the list data source comes from the select and is always taken from there.
/// </summary>
public sealed class ModuleFormViewModel
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Chosen when the module is created; on edit it is always the saved type (it cannot change).</summary>
    public ModuleType Type { get; set; } = ModuleType.Table;
    public string? Description { get; set; }
    public Guid? GroupId { get; set; }

    /// <summary>When filled, a group with this name is created on save and used instead of <see cref="GroupId"/>.</summary>
    public string? NewGroupName { get; set; }

    public Guid? DataSourceId { get; set; }

    /// <summary>Page modules: the HTML (sanitized when saved). <c>{{module:GUID}}</c> embeds a Table or Form module.</summary>
    public string? PageHtml { get; set; }

    /// <summary>Table and Form modules a Page may embed, for the "Sisipkan module" helper.</summary>
    public IReadOnlyList<ModuleSummary> EmbeddableModules { get; set; } = [];

    /// <summary>Optional record actions: Detail is a Query data source, the other three are Command data sources.</summary>
    public Guid? DetailDataSourceId { get; set; }
    public Guid? InsertDataSourceId { get; set; }
    public Guid? UpdateDataSourceId { get; set; }
    public Guid? DeleteDataSourceId { get; set; }

    public string ConfigJson { get; set; } = "{}";

    // Display only.
    public ModuleStatus Status { get; set; } = ModuleStatus.Draft;
    public IReadOnlyList<ModuleGroup> Groups { get; set; } = [];
    public IReadOnlyList<DataSourceOption> DataSources { get; set; } = [];

    /// <summary>Command data sources, for Insert / Update / Delete.</summary>
    public IReadOnlyList<DataSourceOption> CommandDataSources { get; set; } = [];

    /// <summary>Rakit:Db:AllowWrite. When off, Command data sources are refused at runtime and the page shows no write buttons.</summary>
    public bool WriteEnabled { get; set; }

    /// <summary>The saved list data source is gone or is no longer a Query data source.</summary>
    public bool DataSourceUnavailable { get; set; }

    public bool IsEdit => Id is not null;
}

public sealed class PageHtmlPreviewRequest
{
    public string? Html { get; set; }
}

public sealed class ModuleLoadColumnsRequest
{
    public Guid DataSourceId { get; set; }

    /// <summary>Optional sample record key (RouteValue <c>id</c>), so a Detail data source returns a row whose values suggest field types.</summary>
    public string? Key { get; set; }
}

/// <summary>Body of POST /Builder/Module/Preview: the form's own (unsaved) state.</summary>
public sealed class ModulePreviewRequest
{
    public string? Name { get; set; }
    public Guid? DataSourceId { get; set; }

    /// <summary>Same JSON the form saves in ConfigJson (display + access).</summary>
    public JsonElement Config { get; set; }

    /// <summary>Query-string text standing in for the URL of the page, e.g. <c>id=2&amp;q=abc</c>.</summary>
    public string? Query { get; set; }

    public int? Skip { get; set; }
    public int? Take { get; set; }
    public string? OrderBy { get; set; }
    public string? Search { get; set; }
}

public sealed record PreviewColumn(string Field, string Label, bool Sortable, string Format);

/// <summary>Cells are formatted with the same <see cref="ModuleCellFormatter"/> as the runtime page, so the preview looks like the page.</summary>
public sealed record ModulePreviewResponse(
    IReadOnlyList<PreviewColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int? TotalCount,
    int Skip,
    int Take,
    bool HasNext,
    string? OrderBy);

public sealed record LoadedColumn(string Name, string SuggestedFormat);

public static class ModuleDisplayText
{
    public static string StatusLabel(ModuleStatus status) => status == ModuleStatus.Published ? "Published" : "Draft";

    public static string TypeLabel(ModuleType type) => type switch
    {
        ModuleType.Table => "Table",
        ModuleType.Form => "Form",
        ModuleType.Page => "Page / Html",
        _ => type.ToString(),
    };

    public static string ModeLabel(DataSourceMode mode) => DataSourceDisplay.ModeLabel(mode);

    /// <summary>camelCase name used in the stored config ("dateTime").</summary>
    public static string FormatName(ColumnFormat format) => JsonNamingPolicy.CamelCase.ConvertName(format.ToString());

    /// <summary>Guess a display format from the first non-null value of a column.</summary>
    public static string SuggestFormat(object? sample) => sample switch
    {
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? "date" : "dateTime",
        DateTimeOffset => "dateTime",
        bool => "boolean",
        byte or short or int or long or float or double or decimal => "number",
        _ => "text",
    };
}
