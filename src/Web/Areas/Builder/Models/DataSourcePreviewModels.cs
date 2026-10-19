using System.Globalization;
using System.Text.Json;

namespace Rakit.Web.Areas.Builder.Models;

/// <summary>Body of POST /Builder/DataSource/Preview. The definition is the same JSON the form saves.</summary>
public sealed class DataSourcePreviewRequest
{
    /// <summary>"Table" or "Query" (case-insensitive).</summary>
    public string Mode { get; set; } = "";

    /// <summary>"Query" or "Command" (case-insensitive).</summary>
    public string StatementType { get; set; } = "Query";

    public JsonElement Definition { get; set; }

    /// <summary>Sample value per parameter name. Empty or missing = null.</summary>
    public Dictionary<string, string?>? Samples { get; set; }
}

public sealed record PreviewResultSet(
    IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, bool Truncated);

public sealed record PreviewResponse(IReadOnlyList<PreviewResultSet> ResultSets, int? TotalCount, long DurationMs);

public static class PreviewFormat
{
    /// <summary>Display text for one cell. Null stays null so the browser can show it as NULL.</summary>
    public static string? Cell(object? value) => value switch
    {
        null => null,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] bytes => $"0x… ({bytes.Length} byte)",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
