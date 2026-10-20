using System.Globalization;

namespace Rakit.Core.Modules;

/// <summary>
/// Turns a raw result cell into the text a module page shows, following the column's configured format.
/// Null stays null (the page decides how to show "no value"). A value that does not fit its format (a string in a
/// number column, ...) is shown as plain text instead of failing.
/// </summary>
public static class ModuleCellFormatter
{
    /// <summary>Number formatting culture (Indonesian: 1.234,5). Falls back to the invariant culture when the host has no culture data.</summary>
    public static CultureInfo Culture { get; } = ResolveCulture();

    public static string? Format(object? value, ColumnFormat format)
    {
        if (value is null) return null;

        return format switch
        {
            ColumnFormat.Number => FormatNumber(value) ?? Plain(value),
            ColumnFormat.Date => value switch
            {
                DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeOffset d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => Plain(value),
            },
            ColumnFormat.DateTime => value switch
            {
                DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                _ => Plain(value),
            },
            ColumnFormat.Boolean => value is bool b ? (b ? "Ya" : "Tidak") : Plain(value),
            _ => Plain(value),
        };
    }

    /// <summary>Format text for a column without any special format (also the fallback for the others).</summary>
    public static string? Plain(object? value) => value switch
    {
        null => null,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] bytes => $"0x… ({bytes.Length} byte)",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string? FormatNumber(object value) => value switch
    {
        byte or sbyte or short or ushort or int or uint or long or ulong
            => ((IFormattable)value).ToString("#,##0", Culture),
        decimal or float or double
            => ((IFormattable)value).ToString("#,##0.##########", Culture),
        _ => null,
    };

    private static CultureInfo ResolveCulture()
    {
        try
        {
            return CultureInfo.GetCultureInfo("id-ID");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
