using System.Globalization;

namespace Rakit.Core.Modules;

/// <summary>
/// Whether typed text is acceptable for a form field type. Always the invariant culture, like the data source parameter parsing.
/// Blank text is the caller's business (required / NULL), so it is not judged here.
/// </summary>
public static class ModuleFieldValue
{
    public static bool IsValid(FormFieldType type, string text) => type switch
    {
        FormFieldType.Text or FormFieldType.Textarea => true,
        FormFieldType.Number => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        FormFieldType.Date => DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        FormFieldType.DateTime => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        FormFieldType.Boolean => text.Equals("true", StringComparison.OrdinalIgnoreCase)
                                 || text.Equals("false", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public static bool IsText(FormFieldType type) => type is FormFieldType.Text or FormFieldType.Textarea;
}
