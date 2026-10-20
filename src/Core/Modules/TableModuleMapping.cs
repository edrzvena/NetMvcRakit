using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rakit.Core.Modules;

/// <summary>Pure rules of the table wizard: which SQL types can be listed / written and how they map to display formats and form fields, plus names.</summary>
public static partial class TableModuleMapping
{
    public const int MaxTextFieldLength = ModuleInputValidator.MaxFieldTextLength;

    /// <summary>A textarea instead of a text box from this many characters.</summary>
    public const int TextareaFrom = 256;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PlainName();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    private static readonly HashSet<string> NotListable = new(StringComparer.OrdinalIgnoreCase)
    {
        "binary", "varbinary", "image", "timestamp", "rowversion", "xml", "geography", "geometry", "hierarchyid", "sql_variant",
    };

    private static readonly HashSet<string> NotWritable = new(StringComparer.OrdinalIgnoreCase) { "text", "ntext" };

    private static readonly HashSet<string> Numbers = new(StringComparer.OrdinalIgnoreCase)
        { "int", "bigint", "smallint", "tinyint", "decimal", "numeric", "money", "smallmoney", "float", "real" };

    private static readonly HashSet<string> DateTimes = new(StringComparer.OrdinalIgnoreCase)
        { "datetime", "datetime2", "smalldatetime", "datetimeoffset" };

    /// <summary>"nvarchar(100)" → "nvarchar".</summary>
    public static string BaseType(string formattedType)
    {
        var cut = formattedType.IndexOf('(');
        return (cut < 0 ? formattedType : formattedType[..cut]).Trim().ToLowerInvariant();
    }

    /// <summary>Declared length of a character type: 100 for "nvarchar(100)", -1 for "(max)", null when the type has none.</summary>
    public static int? Length(string formattedType)
    {
        var open = formattedType.IndexOf('(');
        var close = formattedType.IndexOf(')');
        if (open < 0 || close < open) return null;
        var text = formattedType[(open + 1)..close].Trim();
        if (text.Equals("max", StringComparison.OrdinalIgnoreCase)) return -1;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public static bool IsListable(string baseType) => !NotListable.Contains(baseType);

    public static bool IsWritableType(string baseType) => IsListable(baseType) && !NotWritable.Contains(baseType);

    /// <summary>Types a generated filter can compare with one value: text (contains), numbers, date, bit and guid (equals).</summary>
    public static bool IsFilterable(string baseType)
        => baseType is "char" or "varchar" or "nchar" or "nvarchar" or "date" or "bit" or "uniqueidentifier" || Numbers.Contains(baseType);

    /// <summary>The query-string name of a column's generated filter ("f_" + the column), or null when the name does not fit.</summary>
    public static string? FilterName(string column)
    {
        var name = "f_" + column;
        return PlainName().IsMatch(name) && name.Length <= ModuleFilter.MaxNameLength ? name : null;
    }

    public static bool IsPlainName(string name) => PlainName().IsMatch(name) && name.Length <= ModuleInputValidator.MaxFieldLength;

    /// <summary>camelCase display format name: text, number, date, dateTime, boolean.</summary>
    public static ColumnFormat Format(string baseType)
        => Numbers.Contains(baseType) ? ColumnFormat.Number
            : baseType == "date" ? ColumnFormat.Date
            : DateTimes.Contains(baseType) ? ColumnFormat.DateTime
            : baseType == "bit" ? ColumnFormat.Boolean
            : ColumnFormat.Text;

    public static FormFieldType FieldType(string formattedType)
    {
        var baseType = BaseType(formattedType);
        if (Numbers.Contains(baseType)) return FormFieldType.Number;
        if (baseType == "date") return FormFieldType.Date;
        if (DateTimes.Contains(baseType)) return FormFieldType.DateTime;
        if (baseType == "bit") return FormFieldType.Boolean;
        if (baseType is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext")
        {
            var length = Length(formattedType);
            return length is null or < 0 or >= TextareaFrom ? FormFieldType.Textarea : FormFieldType.Text;
        }
        return FormFieldType.Text;
    }

    /// <summary>The form field's MaxLength: the declared length of a character column (none for max, or above the validator's cap).</summary>
    public static int? MaxLength(string formattedType)
        => BaseType(formattedType) is "char" or "varchar" or "nchar" or "nvarchar" && Length(formattedType) is { } n and > 0 and <= MaxTextFieldLength ? n : null;

    /// <summary>"CustomerID" → "Customer ID", "created_at" → "Created at".</summary>
    public static string Label(string name)
    {
        var text = WordBoundary().Replace(name.Replace('_', ' ').Trim(), " ");
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ---- names -----------------------------------------------------------------------------------

    /// <summary>
    /// Data source code stem for a table: lower-case letters, digits, '-' and '_' only, starting with a letter, at most 40
    /// characters (the longest suffix, "-insert", keeps it inside the 50 of a data source code). A schema other than the
    /// first allowed one becomes a prefix so two schemas with the same table name do not share data sources.
    /// </summary>
    public static string CodeStem(string schema, string table, bool prefixSchema)
    {
        var text = (prefixSchema ? schema + "-" + table : table).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in text) sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? c : '-');
        var stem = sb.ToString().Trim('-');
        if (stem.Length == 0 || !char.IsAsciiLetter(stem[0])) stem = "t" + stem;
        return stem.Length > 40 ? stem[..40].TrimEnd('-') : stem;
    }

    /// <summary>"Sp" + the table name reduced to letters, digits and '_' + "_" + the operation, e.g. SpCustomers_Insert.</summary>
    public static string ProcedureName(string table, string operation)
    {
        var sb = new StringBuilder();
        foreach (var c in table) if (char.IsAsciiLetterOrDigit(c) || c == '_') sb.Append(c);
        var stem = sb.Length == 0 ? "Table" : sb.ToString();
        if (stem.Length > 100) stem = stem[..100];
        return $"Sp{stem}_{operation}";
    }
}
