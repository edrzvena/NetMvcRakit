using System.Text;
using Rakit.Core.Schema;

namespace Rakit.Core.DataSources;

/// <summary>The table definition cannot be turned into SQL (unknown column, bad operator, ...). Safe to show to the builder.</summary>
public sealed class TableDefinitionException(string message) : Exception(message);

/// <param name="Skip">Rows to skip (&gt;= 0).</param>
/// <param name="Take">Page size; clamped to 1..<see cref="TableQueryRequest.MaxTake"/>.</param>
/// <param name="OrderBy">Optional <c>column_asc</c> / <c>column_desc</c> (the SP convention). Unknown values fall back to the data source's own sort.</param>
/// <param name="Search">Free text matched (contains) against every text column; empty = no search.</param>
public sealed record TableQueryRequest(int Skip = 0, int Take = 10, string? OrderBy = null, string? Search = null)
{
    public const int MaxTake = 1000;
}

/// <param name="DataSql">Page of rows: ORDER BY + OFFSET/FETCH.</param>
/// <param name="CountSql">Total rows matching the same filters/search, in a column named <c>Number</c>.</param>
/// <param name="PagingArgs">Values for <c>@Skip</c>, <c>@Take</c>, <c>@Search</c>; merge with the resolved filter parameters.</param>
/// <param name="FilterParameters">Mapped parameter names the filters read (their values come from the parameter resolver).</param>
/// <param name="FilterTypes">
/// Catalog type of the column a filter parameter is compared with (=, &lt;&gt;, &lt;, ...), so text from a URL can be turned
/// into that type first. Left out for a parameter used only by <c>like</c>, or compared with columns of different types.
/// </param>
public sealed record TableQuery(
    string DataSql,
    string CountSql,
    IReadOnlyDictionary<string, object?> PagingArgs,
    IReadOnlyList<string> FilterParameters,
    IReadOnlyDictionary<string, string> FilterTypes);

public interface ITableQueryBuilder
{
    /// <exception cref="TableDefinitionException"/>
    /// <exception cref="SchemaAccessDeniedException">Schema is not in AllowedSchemas.</exception>
    /// <exception cref="SchemaObjectNotFoundException">No such table/view.</exception>
    Task<TableQuery> BuildAsync(TableDefinition table, TableQueryRequest request, CancellationToken ct = default);
}

public sealed class TableQueryBuilder(ISchemaBrowserService schema) : ITableQueryBuilder
{
    private static readonly IReadOnlyDictionary<string, string> Operators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = "=", ["<>"] = "<>", [">"] = ">", [">="] = ">=", ["<"] = "<", ["<="] = "<=",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { "char", "varchar", "nchar", "nvarchar", "text", "ntext" };

    private static readonly HashSet<string> NonUnicodeTextTypes = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar" };

    private static readonly string[] ReservedParameters = ["Skip", "Take", "Search"];

    public async Task<TableQuery> BuildAsync(TableDefinition table, TableQueryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Skip < 0) throw new ArgumentOutOfRangeException(nameof(request), "Skip tidak boleh negatif.");

        // Throws if the schema is not allowed or the object does not exist; names come back as the catalog spells them.
        var catalog = await schema.GetColumnsAsync(table.Schema, table.Name, ct);
        var tables = await schema.GetTablesAsync(ct);
        var self = tables.FirstOrDefault(t =>
            string.Equals(t.Schema, table.Schema.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Name, table.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new SchemaObjectNotFoundException("Object not found.");

        var byName = catalog.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        ColumnInfo Column(string name, string role) => byName.TryGetValue(name?.Trim() ?? "", out var c)
            ? c
            : throw new TableDefinitionException($"Kolom {role} '{name}' tidak ada di {self.Schema}.{self.Name}.");

        var selected = table.Columns.Count == 0
            ? catalog.ToList()
            : table.Columns.Select(c => Column(c, "pilihan")).DistinctBy(c => c.Name).ToList();

        var where = new List<string>();
        var filterParameters = new List<string>();
        var filterTypes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); // null = columns of different types
        foreach (var f in table.Filters)
        {
            var col = Column(f.Column, "filter");
            var p = (f.Parameter ?? "").Trim();
            if (!DataSourceInputValidator.ParameterNamePattern().IsMatch(p))
                throw new TableDefinitionException($"Filter pada kolom '{col.Name}': nama parameter '{f.Parameter}' tidak valid.");
            if (ReservedParameters.Contains(p, StringComparer.OrdinalIgnoreCase))
                throw new TableDefinitionException($"Filter pada kolom '{col.Name}': nama parameter '{p}' dicadangkan (Skip, Take, Search).");

            var op = (f.Operator ?? "").Trim();
            string condition;
            if (op.Equals("like", StringComparison.OrdinalIgnoreCase))
            {
                condition = $"{Quote(col.Name)} LIKE {ContainsPattern("@" + p)} ESCAPE N'\\'";
            }
            else if (Operators.TryGetValue(op, out var sqlOp))
            {
                // Text arrives as nvarchar; against a char/varchar column that would convert the column and rule out its index.
                var value = NonUnicodeTextTypes.Contains(col.DataType) ? $"CAST(@{p} AS varchar(8000))" : "@" + p;
                condition = $"{Quote(col.Name)} {sqlOp} {value}";
                filterTypes[p] = filterTypes.TryGetValue(p, out var seen) && !string.Equals(seen, col.DataType, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : col.DataType;
            }
            else
            {
                throw new TableDefinitionException($"Operator '{f.Operator}' tidak didukung. Gunakan: =, <>, >, >=, <, <=, like.");
            }

            // A null parameter means "no filter", so optional filters need no special handling by the caller.
            where.Add($"(@{p} IS NULL OR {condition})");
            if (!filterParameters.Contains(p, StringComparer.OrdinalIgnoreCase)) filterParameters.Add(p);
        }

        var textColumns = selected.Where(c => TextTypes.Contains(c.DataType)).ToList();
        if (textColumns.Count > 0)
        {
            var any = string.Join(" OR ", textColumns.Select(c => $"{Quote(c.Name)} LIKE {ContainsPattern("@Search")} ESCAPE N'\\'"));
            where.Add($"(@Search = N'' OR {any})");
        }

        var from = $"{Quote(self.Schema)}.{Quote(self.Name)}";
        var whereSql = where.Count == 0 ? "" : "\nWHERE " + string.Join("\n  AND ", where);

        var orderSql = BuildOrderBy(request.OrderBy, table.Sort, byName, Column, selected);

        var dataSql = new StringBuilder()
            .Append("SELECT ").Append(string.Join(", ", selected.Select(c => Quote(c.Name))))
            .Append("\nFROM ").Append(from)
            .Append(whereSql)
            .Append("\nORDER BY ").Append(orderSql)
            .Append("\nOFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY")
            .ToString();

        var countSql = $"SELECT COUNT(1) AS Number\nFROM {from}{whereSql}";

        var paging = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skip"] = request.Skip,
            ["Take"] = Math.Clamp(request.Take, 1, TableQueryRequest.MaxTake),
            ["Search"] = request.Search?.Trim() ?? "",
        };

        var typed = filterTypes.Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
        return new TableQuery(dataSql, countSql, paging, filterParameters, typed);
    }

    private static string BuildOrderBy(
        string? requested,
        IReadOnlyList<TableSort> defaults,
        IReadOnlyDictionary<string, ColumnInfo> byName,
        Func<string, string, ColumnInfo> column,
        IReadOnlyList<ColumnInfo> selected)
    {
        // "name_desc" -> column "name", descending. Anything we cannot match is ignored, not an error.
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var cut = requested.Trim().LastIndexOf('_');
            if (cut > 0)
            {
                var dir = requested.Trim()[(cut + 1)..];
                var name = requested.Trim()[..cut];
                var isAsc = dir.Equals("asc", StringComparison.OrdinalIgnoreCase);
                var isDesc = dir.Equals("desc", StringComparison.OrdinalIgnoreCase);
                if ((isAsc || isDesc) && byName.TryGetValue(name, out var c) && selected.Any(s => s.Name == c.Name))
                    return $"{Quote(c.Name)} {(isDesc ? "DESC" : "ASC")}";
            }
        }

        if (defaults.Count > 0)
            return string.Join(", ", defaults.Select(s =>
            {
                var c = column(s.Column, "urutan");
                return $"{Quote(c.Name)} {(s.Descending ? "DESC" : "ASC")}";
            }));

        // OFFSET/FETCH needs an ORDER BY; no sort configured means "any stable order is fine".
        return "(SELECT NULL)";
    }

    /// <summary>Quotes a catalog identifier. Braces are refused because the SQL later goes through the {param} template.</summary>
    private static string Quote(string identifier)
    {
        if (identifier.Contains('{') || identifier.Contains('}'))
            throw new TableDefinitionException($"Nama '{identifier}' mengandung kurung kurawal dan tidak didukung.");
        return "[" + identifier.Replace("]", "]]") + "]";
    }

    /// <summary>'%' + value with LIKE wildcards neutralised + '%' (contains).</summary>
    private static string ContainsPattern(string parameter)
        => $"N'%' + REPLACE(REPLACE(REPLACE(REPLACE({parameter}, N'\\', N'\\\\'), N'%', N'\\%'), N'_', N'\\_'), N'[', N'\\[') + N'%'";
}
