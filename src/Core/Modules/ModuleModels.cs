namespace Rakit.Core.Modules;

/// <summary>Stored in rakit.Modules.Type as table / form / page.</summary>
public enum ModuleType { Table, Form, Page, Chart, Widget }

public enum ModuleStatus { Draft, Published }

/// <summary>Serialised camelCase: text, number, date, dateTime, boolean.</summary>
public enum ColumnFormat { Text, Number, Date, DateTime, Boolean }

/// <summary>Which data source feeds a part of the module. The data source owns mode, procedure, parameters and flags.</summary>
public sealed class DataSourceRef
{
    public Guid DataSourceId { get; set; }
}

/// <summary>
/// List feeds the grid. The rest are optional and add a button each: Detail (a Query) loads one record for editing,
/// Insert / Update / Delete (Commands) write it. Record values reach them as FormField mappings, the record key as RouteValue "id".
/// </summary>
public sealed class ModuleSource
{
    public DataSourceRef? List { get; set; }
    public DataSourceRef? Detail { get; set; }
    public DataSourceRef? Insert { get; set; }
    public DataSourceRef? Update { get; set; }
    public DataSourceRef? Delete { get; set; }
}

/// <summary>
/// Serialised camelCase: text, textarea, number, date, dateTime, boolean, select, browse, email, url, section.
/// A <see cref="Section"/> is a heading between fields, not a field: it carries no value.
/// </summary>
public enum FormFieldType { Text, Textarea, Number, Date, DateTime, Boolean, Select, Browse, Email, Url, Section }

/// <summary>A fixed choice of a select field or filter (instead of a lookup data source).</summary>
public sealed class ModuleOption
{
    public string Value { get; set; } = "";
    public string? Text { get; set; }
}

/// <summary>
/// Where the choices of a <see cref="FormFieldType.Select"/> / <see cref="FormFieldType.Browse"/> field come from: a Query
/// data source whose result has a <see cref="ValueField"/> column (what is stored, as text) and a <see cref="TextField"/>
/// column (what the user reads). Column names are matched case-insensitively.
/// </summary>
public sealed class ModuleLookup
{
    public Guid DataSourceId { get; set; }
    public string ValueField { get; set; } = "";
    public string TextField { get; set; } = "";
}

public sealed class ModuleFormField
{
    /// <summary>Time limit for <see cref="Regex"/> so a pattern cannot be used to stall the server.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Form key (the FormField parameter mapping key) and the detail column it is filled from, matched case-insensitively.</summary>
    public string Name { get; set; } = "";

    /// <summary>Caption; blank = the field name.</summary>
    public string? Label { get; set; }

    public FormFieldType Type { get; set; } = FormFieldType.Text;
    public bool Required { get; set; }

    /// <summary>Text and textarea only.</summary>
    public int? MaxLength { get; set; }

    /// <summary>Text and textarea only; the whole value must match.</summary>
    public string? Regex { get; set; }

    /// <summary>Initial value of a new record, written as it would be typed (dates as yyyy-MM-dd).</summary>
    public string? Default { get; set; }

    /// <summary>Shown but never accepted from the client.</summary>
    public bool Readonly { get; set; }

    /// <summary>Select and browse fields (a select may use <see cref="Options"/> instead).</summary>
    public ModuleLookup? Lookup { get; set; }

    public const int MaxPlaceholderLength = 200;
    public const int MaxHelpLength = 300;
    public const int MaxOptions = 200;

    /// <summary>Grey hint inside an empty input.</summary>
    public string? Placeholder { get; set; }

    /// <summary>Small text under the input; for a section, the text under the heading.</summary>
    public string? Help { get; set; }

    /// <summary>Width on a wide screen in Bootstrap's 12 columns (1-12); null = the whole row.</summary>
    public int? ColSize { get; set; }

    /// <summary>Number, date and dateTime fields: smallest and largest accepted value, written as it would be typed.</summary>
    public string? Min { get; set; }
    public string? Max { get; set; }

    /// <summary>Number fields: the step of the input (a number above 0).</summary>
    public string? Step { get; set; }

    /// <summary>Select fields: fixed choices. When filled, the field needs no lookup and only these values are accepted.</summary>
    public List<ModuleOption>? Options { get; set; }

    /// <summary>A heading, not a data field.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSection => Type == FormFieldType.Section;
}

/// <summary>
/// A Widget module: one KPI card read from the first row of its (Query) data source. The label is a fixed text or, when
/// <see cref="LabelField"/> is set, a column of that row. <see cref="Format"/> uses the same formats as a Table column.
/// </summary>
public sealed class ModuleWidget
{
    public const int MaxFieldLength = 128;
    public const int MaxLabelLength = 100;
    public const int MaxAffixLength = 20;
    public const int MaxDecimals = 6;
    public const int MinRefreshSeconds = 10;
    public const int MaxRefreshSeconds = 3600;

    /// <summary>The Bootstrap colour names a card can use (camelCase / lower-case in JSON).</summary>
    public static readonly IReadOnlyList<string> Colors = ["primary", "success", "warning", "danger", "info", "secondary", "dark"];

    /// <summary>Result column holding the value; matched case-insensitively.</summary>
    public string ValueField { get; set; } = "";
    public string? Label { get; set; }
    public string? LabelField { get; set; }
    public ColumnFormat Format { get; set; } = ColumnFormat.Number;

    /// <summary>Decimal places of a number value (0..6).</summary>
    public int Decimals { get; set; }
    public string? Prefix { get; set; }
    public string? Suffix { get; set; }
    public string Color { get; set; } = "primary";

    /// <summary>0 = never; otherwise 10..3600 seconds between automatic reloads.</summary>
    public int RefreshSeconds { get; set; }
}

/// <summary>Serialised camelCase: bar, line, area, pie, doughnut.</summary>
public enum ChartKind { Bar, Line, Area, Pie, Doughnut }

public sealed class ChartSeries
{
    /// <summary>Result column holding this series' numbers; matched case-insensitively.</summary>
    public string ValueField { get; set; } = "";

    /// <summary>Legend text; blank = the column name.</summary>
    public string? Label { get; set; }

    /// <summary><c>#RRGGBB</c>; blank = the default palette.</summary>
    public string? Color { get; set; }
}

/// <summary>A Chart module: categories from <see cref="LabelField"/>, one or more series of numbers, from a (Query) data source.</summary>
public sealed class ModuleChart
{
    public const int MaxSeries = 8;
    public const int DefaultMaxRows = 100;
    public const int HardMaxRows = 1000;
    public const int MaxTitleLength = 100;

    public ChartKind Kind { get; set; } = ChartKind.Bar;

    /// <summary>Result column holding the category (x axis / slice) names.</summary>
    public string LabelField { get; set; } = "";

    public List<ChartSeries> Series { get; set; } = [];

    /// <summary>Bar, line and area only.</summary>
    public bool Stacked { get; set; }

    /// <summary>Bar only: horizontal bars.</summary>
    public bool Horizontal { get; set; }

    public bool Legend { get; set; } = true;
    public string? Title { get; set; }

    /// <summary>Rows read from the data source (1..1000).</summary>
    public int MaxRows { get; set; } = DefaultMaxRows;
}

/// <summary>Content of a Page module: HTML written in the builder; <c>{{module:GUID}}</c> embeds another module.</summary>
public sealed class ModulePage
{
    public const int MaxHtmlLength = 200_000;
    public string Html { get; set; } = "";
}

public sealed class ModuleForm
{
    public List<ModuleFormField> Fields { get; set; } = [];

    /// <summary>The fields that hold a value (everything but the section headings), in order.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<ModuleFormField> DataFields => Fields.Where(f => !f.IsSection);
}

public sealed class ModuleColumn
{
    /// <summary>Result column name, matched case-insensitively.</summary>
    public string Field { get; set; } = "";

    /// <summary>Header text; blank = the field name.</summary>
    public string? Label { get; set; }

    public bool Visible { get; set; } = true;
    public bool Sortable { get; set; }
    public ColumnFormat Format { get; set; } = ColumnFormat.Text;
}

/// <summary>Which controls the runtime page offers. Everything but Detail is on by default (the page always had them).</summary>
public sealed class ModuleButtons
{
    public bool Search { get; set; } = true;

    /// <summary>The filter panel button; only shown when the module has filters.</summary>
    public bool Filter { get; set; } = true;

    public bool Add { get; set; } = true;
    public bool Edit { get; set; } = true;
    public bool Delete { get; set; } = true;

    /// <summary>A read-only "Lihat" button on every row (needs the Detail data source and the key column).</summary>
    public bool Detail { get; set; }
}

/// <summary>The page's "more" menu. All off by default.</summary>
public sealed class ModuleMenu
{
    public bool Refresh { get; set; }

    /// <summary>CSV of the rows on the page.</summary>
    public bool Export { get; set; }

    /// <summary>CSV of every row that matches the search and filters (up to <see cref="ModuleExport.MaxRows"/>).</summary>
    public bool ExportAll { get; set; }

    /// <summary>CSV upload, one Insert per row (needs the Insert data source and at least one form field).</summary>
    public bool Import { get; set; }
}

/// <summary>Limits of the CSV export and import of a Table module.</summary>
public static class ModuleExport
{
    public const int MaxRows = 10_000;
    public const int ChunkRows = ModuleDisplay.HardMaxTake;
    public const int MaxImportRows = 500;
    public const int MaxImportBytes = 1_048_576;
}

/// <summary>
/// An input of the filter panel. Its <see cref="Name"/> is the query-string key the value travels under (and what the data
/// source maps a parameter to); which comparison is made is up to the data source. A select filter takes its choices from a
/// lookup data source or from fixed <see cref="Options"/>.
/// </summary>
public sealed class ModuleFilter
{
    public const int MaxPerModule = 10;
    public const int MaxNameLength = 64;
    public const int MaxLabelLength = 100;

    /// <summary>The filter types: <see cref="FormFieldType"/> restricted to these.</summary>
    public static readonly IReadOnlyList<FormFieldType> Types =
        [FormFieldType.Text, FormFieldType.Number, FormFieldType.Date, FormFieldType.DateTime, FormFieldType.Boolean, FormFieldType.Select];

    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public FormFieldType Type { get; set; } = FormFieldType.Text;
    public string? Placeholder { get; set; }
    public ModuleLookup? Lookup { get; set; }
    public List<ModuleOption>? Options { get; set; }
}

public sealed class ModuleDisplay
{
    public const int DefaultTake = 10;
    public const int DefaultMaxTake = 100;
    public const int HardMaxTake = 1000;
    public const int MaxTitleLength = 200;
    public const int MaxSearchPlaceholderLength = 100;

    /// <summary>Heading of the page; blank = the module's name.</summary>
    public string? Title { get; set; }

    /// <summary>Hint in the search box; blank = "Cari...".</summary>
    public string? SearchPlaceholder { get; set; }

    public ModuleButtons Buttons { get; set; } = new();
    public ModuleMenu Menu { get; set; } = new();

    /// <summary>The filter panel, in order.</summary>
    public List<ModuleFilter> Filters { get; set; } = [];

    public int Take { get; set; } = DefaultTake;

    /// <summary>Largest page size a visitor may ask for.</summary>
    public int MaxTake { get; set; } = DefaultMaxTake;

    /// <summary>List column holding the record key; required when the module has Detail, Update or Delete.</summary>
    public string? KeyField { get; set; }

    /// <summary>Empty = every result column, in the order the data source returns them.</summary>
    public List<ModuleColumn> Columns { get; set; } = [];
}

/// <summary>
/// Which record buttons a runtime page offers. Each needs its data source; the write ones also need Rakit:Db:AllowWrite
/// (the data source service refuses Command statements without it anyway, so the buttons would only fail).
/// Edit needs both Detail (to fill the form) and Update.
/// </summary>
public sealed record ModuleRecordActions(bool CanAdd, bool CanEdit, bool CanDelete, bool CanView = false, bool CanImport = false)
{
    /// <summary>
    /// The page's <c>display.buttons</c> can switch a button off but never on: a data source and (for writes) the AllowWrite flag are
    /// still needed. "Lihat" needs only the Detail data source and the key; Import is a menu item, independent of the Tambah button.
    /// </summary>
    public static ModuleRecordActions For(ModuleConfig config, bool allowWrite)
    {
        var source = config.Source;
        var buttons = config.Display.Buttons;
        var hasFields = config.Form.DataFields.Any();
        var hasKey = !string.IsNullOrWhiteSpace(config.Display.KeyField);
        return new ModuleRecordActions(
            CanAdd: allowWrite && buttons.Add && source.Insert is not null && hasFields,
            CanEdit: allowWrite && buttons.Edit && source.Update is not null && source.Detail is not null && hasFields && hasKey,
            CanDelete: allowWrite && buttons.Delete && source.Delete is not null && hasKey,
            CanView: buttons.Detail && source.Detail is not null && hasKey,
            CanImport: allowWrite && config.Display.Menu.Import && source.Insert is not null && hasFields);
    }
}

/// <summary>
/// A button that runs a workflow. On a Table module it sits in the toolbar, or on every row (<see cref="PerRow"/>, the row's
/// record key becomes RouteValue <c>id</c>); on a Form module it sits next to Save and sends the (validated) form values.
/// </summary>
public sealed class ModuleAction
{
    public const int MaxCodeLength = 50;
    public const int MaxLabelLength = 100;
    public const int MaxConfirmLength = 200;
    public const int MaxPerModule = 10;

    /// <summary>The Bootstrap button colours an action can use (lower-case in JSON).</summary>
    public static readonly IReadOnlyList<string> Styles = ["primary", "secondary", "success", "danger", "warning", "info", "dark"];

    /// <summary>Identifies the button in the URL; letters, digits, '_' and '-', starting with a letter.</summary>
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public Guid WorkflowId { get; set; }

    /// <summary>One of <see cref="Styles"/>; blank = primary.</summary>
    public string? Style { get; set; }

    /// <summary>When filled, the visitor must confirm with this text before the workflow runs.</summary>
    public string? Confirm { get; set; }

    /// <summary>Table only: one button per row instead of one in the toolbar. Needs the module's key field.</summary>
    public bool PerRow { get; set; }
}

/// <summary>Stored, not enforced until a login phase exists (no OIDC yet).</summary>
public sealed class ModuleAccess
{
    public List<string> View { get; set; } = ["*"];
}

/// <summary>Stored as JSON in rakit.Modules.Config.</summary>
public sealed class ModuleConfig
{
    public ModuleSource Source { get; set; } = new();
    public ModuleDisplay Display { get; set; } = new();
    public ModuleForm Form { get; set; } = new();

    /// <summary>Page modules only.</summary>
    public ModulePage? Page { get; set; }

    /// <summary>Widget modules only.</summary>
    public ModuleWidget? Widget { get; set; }

    /// <summary>Chart modules only.</summary>
    public ModuleChart? Chart { get; set; }

    /// <summary>Workflow buttons (Table and Form modules only).</summary>
    public List<ModuleAction> Actions { get; set; } = [];
    public ModuleAccess Access { get; set; } = new();
}

/// <summary>What a caller supplies to create or update a module. Status is changed separately.</summary>
public sealed class ModuleInput
{
    public string Name { get; set; } = "";
    public ModuleType Type { get; set; } = ModuleType.Table;
    public string? Description { get; set; }
    public Guid? GroupId { get; set; }
    public ModuleConfig Config { get; set; } = new();
}

public sealed record Module(
    Guid Id,
    string Name,
    ModuleType Type,
    string? Description,
    ModuleStatus Status,
    Guid? GroupId,
    ModuleConfig Config,
    DateTime Created,
    DateTime? Updated);

public sealed record ModuleSummary(
    Guid Id, string Name, ModuleType Type, ModuleStatus Status, Guid? GroupId, string? GroupName, DateTime? Updated);

public sealed record ModuleGroup(Guid Id, string Name, bool IsActive, DateTime Created, DateTime? Updated);

/// <summary>Exactly one of <see cref="ModuleId"/> / <see cref="Url"/> unless <see cref="IsDelimiter"/>.</summary>
public sealed class MenuInput
{
    public string Label { get; set; } = "";
    public string? Icon { get; set; }
    public Guid? ModuleId { get; set; }
    public string? Url { get; set; }
    public Guid? ParentId { get; set; }
    public bool IsDelimiter { get; set; }
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
}

public sealed record Menu(
    Guid Id, string Label, string? Icon, Guid? ModuleId, string? Url, Guid? ParentId,
    bool IsDelimiter, bool IsActive, int Order, DateTime Created, DateTime? Updated);
