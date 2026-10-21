using System.Data;
using System.Globalization;

namespace Rakit.Core.Sql;

/// <summary>Turning text (query strings, forms) into values of a SQL type; shared by procedure parameters and table filters.</summary>
internal static class SqlValues
{
    /// <summary>The SqlDbType of a catalog type name (TYPE_NAME), or null for a type Rakit does not bind (CLR, table types, ...).</summary>
    public static SqlDbType? ToSqlDbType(string typeName) => typeName.ToLowerInvariant() switch
    {
        "bigint" => SqlDbType.BigInt,
        "int" => SqlDbType.Int,
        "smallint" => SqlDbType.SmallInt,
        "tinyint" => SqlDbType.TinyInt,
        "bit" => SqlDbType.Bit,
        "decimal" or "numeric" => SqlDbType.Decimal,
        "money" => SqlDbType.Money,
        "smallmoney" => SqlDbType.SmallMoney,
        "float" => SqlDbType.Float,
        "real" => SqlDbType.Real,
        "date" => SqlDbType.Date,
        "datetime" => SqlDbType.DateTime,
        "datetime2" => SqlDbType.DateTime2,
        "smalldatetime" => SqlDbType.SmallDateTime,
        "datetimeoffset" => SqlDbType.DateTimeOffset,
        "time" => SqlDbType.Time,
        "char" => SqlDbType.Char,
        "varchar" => SqlDbType.VarChar,
        "nchar" => SqlDbType.NChar,
        "nvarchar" => SqlDbType.NVarChar,
        "text" => SqlDbType.Text,
        "ntext" => SqlDbType.NText,
        "binary" => SqlDbType.Binary,
        "varbinary" => SqlDbType.VarBinary,
        "image" => SqlDbType.Image,
        "uniqueidentifier" => SqlDbType.UniqueIdentifier,
        "xml" => SqlDbType.Xml,
        _ => null,
    };

    /// <summary>
    /// Values from query strings and forms arrive as text, but a typed parameter will not convert them itself
    /// (string to uniqueidentifier fails). Text for a non-text parameter is parsed with the invariant culture;
    /// blank text means "no value" (null) rather than the 0 / 1900-01-01 SQL Server would make of ''.
    /// </summary>
    /// <exception cref="ArgumentException">The text is not a valid value of <paramref name="type"/>.</exception>
    public static object FromText(string name, string text, SqlDbType type)
    {
        if (type is SqlDbType.Char or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.NVarChar
            or SqlDbType.Text or SqlDbType.NText or SqlDbType.Xml or SqlDbType.Binary or SqlDbType.VarBinary or SqlDbType.Image)
            return text;

        var trimmed = text.Trim();
        if (trimmed.Length == 0) return DBNull.Value;

        var culture = CultureInfo.InvariantCulture;
        try
        {
            return type switch
            {
                SqlDbType.BigInt => long.Parse(trimmed, NumberStyles.Integer, culture),
                SqlDbType.Int => int.Parse(trimmed, NumberStyles.Integer, culture),
                SqlDbType.SmallInt => short.Parse(trimmed, NumberStyles.Integer, culture),
                SqlDbType.TinyInt => byte.Parse(trimmed, NumberStyles.Integer, culture),
                SqlDbType.Bit => ParseBit(trimmed),
                // with an exponent, like a form's number field accepts it ("1e3")
                SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney
                    => decimal.Parse(trimmed, NumberStyles.Number | NumberStyles.AllowExponent, culture),
                SqlDbType.Float or SqlDbType.Real => double.Parse(trimmed, NumberStyles.Float, culture),
                SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime
                    => DateTime.Parse(trimmed, culture, DateTimeStyles.None),
                SqlDbType.DateTimeOffset => System.DateTimeOffset.Parse(trimmed, culture),
                SqlDbType.Time => TimeSpan.Parse(trimmed, culture),
                SqlDbType.UniqueIdentifier => Guid.Parse(trimmed),
                _ => text,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new ArgumentException($"Nilai parameter '{name}' ('{text}') bukan {type} yang valid.", "args", ex);
        }
    }

    private static bool ParseBit(string text) => text.ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => throw new FormatException("Not a bit value."),
    };
}
