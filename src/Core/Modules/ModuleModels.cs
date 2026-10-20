namespace Rakit.Core.Modules;

public enum ModuleType { Table }

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

/// <summary>Serialised camelCase: text, textarea, number, date, dateTime, boolean.</summary>
public enum FormFieldType { Text, Textarea, Number, Date, DateTime, Boolean }

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
}

public sealed class ModuleForm
{
    public List<ModuleFormField> Fields { get; set; } = [];
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

public sealed class ModuleDisplay
{
    public const int DefaultTake = 10;
    public const int DefaultMaxTake = 100;
    public const int HardMaxTake = 1000;

    public int Take { get; set; } = DefaultTake;

    /// <summary>Largest page size a visitor may ask for.</summary>
    public int MaxTake { get; set; } = DefaultMaxTake;

    /// <summary>List column holding the record key; required when the module has Detail, Update or Delete.</summary>
    public string? KeyField { get; set; }

    /// <summary>Empty = every result column, in the order the data source returns them.</summary>
    public List<ModuleColumn> Columns { get; set; } = [];
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
