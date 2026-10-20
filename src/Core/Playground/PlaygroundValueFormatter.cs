using System.Globalization;
using System.Text;

namespace Rakit.Core.Playground;

/// <summary>Display text for one cell of a Playground result. Null stays null so the browser can show it as NULL.</summary>
public static class PlaygroundValueFormatter
{
    public const int MaxCellLength = 2000;
    private const int MaxBinaryBytes = 32;

    public static string? Format(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case string text:
                return text.Length <= MaxCellLength ? text : text[..MaxCellLength] + "…";
            case DateTime d:
                return d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            case DateTimeOffset d:
                return d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture);
            case DateOnly d:
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeSpan t:
                return t.ToString("c", CultureInfo.InvariantCulture);
            case bool b:
                return b ? "true" : "false";
            case Guid g:
                return g.ToString("D").ToUpperInvariant();
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case byte[] bytes:
                return Hex(bytes);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return Format(value.ToString());
        }
    }

    private static string Hex(byte[] bytes)
    {
        var shown = Math.Min(bytes.Length, MaxBinaryBytes);
        var text = new StringBuilder("0x", 2 + shown * 2);
        for (var i = 0; i < shown; i++) text.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        if (bytes.Length > shown) text.Append("… (").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" byte)");
        return text.ToString();
    }
}
