namespace Rakit.Web.Areas.Object.Models;

public sealed record ObjectColumn(string Field, string Label, bool Sortable, string Format);

/// <summary>
/// One page of a module's list. Cells are already formatted text (null = no value); the browser only shows them.
/// <see cref="Total"/> is null when the data source cannot count.
/// </summary>
/// <param name="Keys">Raw record key per row (same order as <paramref name="Rows"/>), or null when the module has no record actions.</param>
/// <param name="Actions">Which record buttons to show.</param>
public sealed record ObjectPageData(
    IReadOnlyList<ObjectColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int? Total,
    int Skip,
    int Take,
    int MaxTake,
    bool HasNext,
    string? OrderBy,
    string? Search,
    IReadOnlyList<string?>? Keys,
    ObjectActions Actions);

public sealed record ObjectActions(bool CanAdd, bool CanEdit, bool CanDelete);

/// <summary>Field values of one record, keyed by form field name, written the way an input shows them.</summary>
public sealed record ObjectRecord(IReadOnlyDictionary<string, string?> Values);

public sealed record ObjectLookupItem(string Value, string Text);

/// <summary>One page of the choices of a select / browse field. <see cref="Total"/> is null when the data source cannot count.</summary>
public sealed record ObjectLookup(IReadOnlyList<ObjectLookupItem> Items, int? Total, int Skip, int Take, bool HasNext);

/// <param name="Color"><c>#RRGGBB</c> or null (the page's own palette).</param>
/// <param name="Data">One entry per label; null = no number (a gap).</param>
public sealed record ObjectChartSeries(string Label, string? Color, IReadOnlyList<double?> Data);

/// <summary>What a Chart module's data endpoint answers. <see cref="Kind"/> is bar / line / area / pie / doughnut.</summary>
public sealed record ObjectChartData(
    string Kind,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ObjectChartSeries> Series,
    bool Stacked,
    bool Horizontal,
    bool Legend,
    string? Title,
    bool Truncated);

/// <summary>What a Widget module's data endpoint answers. <see cref="Text"/> is formatted on the server; <see cref="Value"/> is the raw value (number, boolean or text).</summary>
public sealed record ObjectWidgetData(object? Value, string? Text, string? Label, string Color, int RefreshSeconds);

/// <summary>A write went through.</summary>
public sealed record ObjectDone(string Message);

/// <param name="Errors">Per form field name; the key "" is about the request as a whole. Only for 400 responses.</param>
public sealed record ObjectError(string Message, IReadOnlyDictionary<string, string>? Errors = null);
