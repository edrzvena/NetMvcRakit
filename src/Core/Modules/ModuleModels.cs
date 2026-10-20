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

public sealed class ModuleSource
{
    public DataSourceRef? List { get; set; }
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
