namespace Rakit.Web.Areas.Object.Models;

public sealed record ObjectColumn(string Field, string Label, bool Sortable, string Format);

/// <summary>
/// One page of a module's list. Cells are already formatted text (null = no value); the browser only shows them.
/// <see cref="Total"/> is null when the data source cannot count.
/// </summary>
public sealed record ObjectPageData(
    IReadOnlyList<ObjectColumn> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int? Total,
    int Skip,
    int Take,
    int MaxTake,
    bool HasNext,
    string? OrderBy,
    string? Search);

public sealed record ObjectError(string Message);
