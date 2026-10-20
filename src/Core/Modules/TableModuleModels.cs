namespace Rakit.Core.Modules;

/// <summary>One column of a table or view as the "Buat dari tabel" wizard shows it.</summary>
/// <param name="Selectable">Can be listed: false for binary, xml and CLR types.</param>
/// <param name="Writable">Can be part of the generated Insert / Update: a listable column that is not identity, computed or a row version, and has a plain name.</param>
/// <param name="Filterable">Can be a filter of the page: a plain name and a text, number, date, boolean or guid type.</param>
/// <param name="Format">Suggested display format (text, number, date, dateTime, boolean).</param>
/// <param name="FieldType">Suggested form field type (text, textarea, number, date, dateTime, boolean).</param>
public sealed record TableColumnChoice(
    string Name, string Type, bool IsNullable, bool IsIdentity, bool IsPrimaryKey, bool HasDefault,
    bool Selectable, bool Writable, bool Filterable, string? Note, string Format, string FieldType);

/// <param name="SuggestedKey">The single-column primary key, or null (none, or a composite key).</param>
/// <param name="CanWrite">A table (not a view): Insert / Update / Delete can be generated.</param>
/// <param name="CanCreateProcedures">Rakit:Playground:Enabled and AllowWrite: the wizard can create the CRUD procedures.</param>
public sealed record TableInspection(
    string Schema, string Name, string Kind,
    IReadOnlyList<TableColumnChoice> Columns,
    string? SuggestedKey, bool HasCompositeKey, bool CanWrite, bool CanCreateProcedures,
    IReadOnlyList<string> Notes);

/// <summary>What the wizard asks for. Insert / Update / Delete each need <c>CanCreateProcedures</c>; Update and Delete also need a key.</summary>
public sealed class TableModuleRequest
{
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Columns shown in the list (and the data source columns). The key column is always added.</summary>
    public List<string> Columns { get; set; } = [];

    public string? KeyColumn { get; set; }

    /// <summary>Columns that become filters of the page (and of the List data source: text = contains, the rest = equals). Must be among <see cref="Columns"/>.</summary>
    public List<string> FilterColumns { get; set; } = [];

    public bool Insert { get; set; }
    public bool Update { get; set; }
    public bool Delete { get; set; }

    /// <summary>A Form module only inserts: no List / Detail data source, just Insert and the form fields.</summary>
    public bool ForForm { get; set; }
}

/// <param name="Role">list, detail, insert, update or delete.</param>
/// <param name="Created">False when a data source with that code existed and got a new version.</param>
public sealed record GeneratedDataSource(Guid Id, string Code, string Name, string Role, Rakit.Core.DataSources.DataSourceMode Mode, bool Created);

/// <param name="Config">Display and Form (and nothing else) to load into the module form; same shape as the stored config.</param>
public sealed record TableModuleResult(
    IReadOnlyList<GeneratedDataSource> DataSources, ModuleConfig Config, IReadOnlyList<string> Notes);

public enum TableModuleErrorKind { Invalid, NeedsPlayground, Database }

/// <summary>The wizard cannot do what was asked. The message is safe to show in the builder.</summary>
public sealed class TableModuleException(string message, TableModuleErrorKind kind) : Exception(message)
{
    public TableModuleErrorKind Kind { get; } = kind;
}
