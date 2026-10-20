using System.Globalization;
using System.Net.Mail;

namespace Rakit.Core.Modules;

/// <summary>
/// Whether typed text is acceptable for a form field type. Always the invariant culture, like the data source parameter parsing.
/// Blank text is the caller's business (required / NULL), so it is not judged here.
/// </summary>
public static class ModuleFieldValue
{
    public const int MaxEmailLength = 254;

    public static bool IsValid(FormFieldType type, string text) => type switch
    {
        FormFieldType.Text or FormFieldType.Textarea or FormFieldType.Select or FormFieldType.Browse => true,
        FormFieldType.Number => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        FormFieldType.Date => DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        FormFieldType.DateTime => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        FormFieldType.Boolean => text.Equals("true", StringComparison.OrdinalIgnoreCase)
                                 || text.Equals("false", StringComparison.OrdinalIgnoreCase),
        FormFieldType.Email => IsEmail(text),
        FormFieldType.Url => IsUrl(text),
        _ => false,
    };

    /// <summary>One plain address (no display name, no spaces).</summary>
    public static bool IsEmail(string text)
        => text.Length <= MaxEmailLength && !text.Any(char.IsWhiteSpace)
           && MailAddress.TryCreate(text, out var address) && string.Equals(address.Address, text, StringComparison.Ordinal)
           && address.Host.Contains('.');

    /// <summary>An absolute http or https address.</summary>
    public static bool IsUrl(string text)
        => !text.Any(char.IsWhiteSpace) && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>
    /// Types whose value is plain text for validation (MaxLength, Regex apply). A select / browse value is the lookup's
    /// value column as text; whether it is a real choice is left to the data source, not checked here.
    /// </summary>
    public static bool IsText(FormFieldType type)
        => type is FormFieldType.Text or FormFieldType.Textarea or FormFieldType.Select or FormFieldType.Browse
            or FormFieldType.Email or FormFieldType.Url;

    /// <summary>Types that can have a Min / Max.</summary>
    public static bool HasRange(FormFieldType type) => type is FormFieldType.Number or FormFieldType.Date or FormFieldType.DateTime;

    /// <summary>
    /// Orders two values of a range type (number, date, dateTime): negative = first is smaller. Null when either is not a valid
    /// value of the type.
    /// </summary>
    public static int? Compare(FormFieldType type, string first, string second)
    {
        switch (type)
        {
            case FormFieldType.Number:
                return decimal.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                       && decimal.TryParse(second, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? a.CompareTo(b) : null;
            case FormFieldType.Date:
                return DateOnly.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)
                       && DateOnly.TryParse(second, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) ? d1.CompareTo(d2) : null;
            case FormFieldType.DateTime:
                return DateTime.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t1)
                       && DateTime.TryParse(second, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2) ? t1.CompareTo(t2) : null;
            default:
                return null;
        }
    }

    /// <summary>A database value as text with the invariant culture (null stays null); used for record keys and lookup values.</summary>
    public static string? ToText(object? value) => value switch
    {
        null => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public static bool IsLookup(FormFieldType type) => type is FormFieldType.Select or FormFieldType.Browse;
}
