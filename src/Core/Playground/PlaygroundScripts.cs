using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rakit.Core.Playground;

/// <summary>
/// T-SQL text the Playground puts in the editor (SELECT, INSERT, EXEC, CREATE TABLE, ...). Names are quoted with [ ] (a ]
/// inside a name is doubled) so they can never end the identifier; nothing here runs SQL.
/// </summary>
public static partial class PlaygroundScripts
{
    private const int MaxColumns = 1024;
    private const int MaxIdentifierLength = 128;

    /// <summary>Column types the table designer offers.</summary>
    public static IReadOnlyList<string> ColumnTypes { get; } =
    [
        "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real",
        "date", "datetime", "datetime2", "smalldatetime", "time", "datetimeoffset",
        "nvarchar", "varchar", "nchar", "char", "varbinary", "binary", "uniqueidentifier", "xml",
    ];

    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string QuoteObject(string schema, string name) => Quote(schema) + "." + Quote(name);

    /// <summary>The type as written in a column or parameter declaration, e.g. <c>nvarchar(100)</c>, <c>decimal(18,2)</c>.</summary>
    internal static string FormatType(string type, int maxLength, byte precision, byte scale) => type.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" => $"{type}({(maxLength < 0 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength < 0 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        "datetime2" or "time" or "datetimeoffset" => scale == 7 ? type : $"{type}({scale})",
        "float" => precision == 53 ? type : $"{type}({precision})",
        _ => type,
    };

    // ---- scripts for an existing object -------------------------------------------------------

    public static string SelectTop(PlaygroundObjectDetail d, int top = 100)
    {
        var head = $"SELECT TOP ({top.ToString(CultureInfo.InvariantCulture)})";
        var from = "FROM " + QuoteObject(d.Schema, d.Name) + ";\n";
        if (d.Columns.Count == 0) return $"{head} *\n{from}";
        return head + "\n" + string.Join(",\n", d.Columns.Select(c => "    " + Quote(c.Name))) + "\n" + from;
    }

    public static string Insert(PlaygroundObjectDetail d)
    {
        var columns = Writable(d).ToList();
        if (columns.Count == 0) return $"-- {QuoteObject(d.Schema, d.Name)} tidak punya kolom yang bisa diisi.\n";

        var sb = new StringBuilder();
        sb.Append("INSERT INTO ").Append(QuoteObject(d.Schema, d.Name)).Append(" (")
            .Append(string.Join(", ", columns.Select(c => Quote(c.Name)))).Append(")\nVALUES (\n");
        AppendItems(sb, columns.Select(c => ("NULL", Note(c))).ToList());
        sb.Append(");\n");
        return sb.ToString();
    }

    public static string Update(PlaygroundObjectDetail d)
    {
        var columns = Writable(d).Where(c => !c.IsPrimaryKey).ToList();
        if (columns.Count == 0) columns = Writable(d).ToList();
        if (columns.Count == 0) return $"-- {QuoteObject(d.Schema, d.Name)} tidak punya kolom yang bisa diubah.\n";

        var sb = new StringBuilder();
        sb.Append("UPDATE ").Append(QuoteObject(d.Schema, d.Name)).Append("\nSET\n");
        AppendItems(sb, columns.Select(c => (Quote(c.Name) + " = NULL", Note(c))).ToList());
        sb.Append(WhereByKey(d));
        return sb.ToString();
    }

    public static string Delete(PlaygroundObjectDetail d)
        => "DELETE FROM " + QuoteObject(d.Schema, d.Name) + "\n" + WhereByKey(d);

    public static string Exec(PlaygroundObjectDetail d)
    {
        var name = QuoteObject(d.Schema, d.Name);
        if (d.Parameters.Count == 0)
            return $"DECLARE @RC int;\nEXEC @RC = {name};\nSELECT @RC AS ReturnValue;\n";

        var sb = new StringBuilder("DECLARE @RC int;\n");
        foreach (var p in d.Parameters.Where(p => p.IsOutput))
            sb.Append("DECLARE ").Append(p.Name).Append(' ').Append(FormatType(p.DataType, p.MaxLength, p.Precision, p.Scale)).Append(";\n");

        sb.Append("EXEC @RC = ").Append(name).Append('\n');
        AppendItems(sb, d.Parameters.Select(p =>
        {
            var type = FormatType(p.DataType, p.MaxLength, p.Precision, p.Scale);
            var item = p.IsOutput ? $"{p.Name} = {p.Name} OUTPUT" : $"{p.Name} = NULL";
            return (item, type + (p.HasDefault && !p.IsOutput ? ", opsional" : ""));
        }).ToList(), indent: "    ", terminator: ";");

        sb.Append("SELECT @RC AS ReturnValue");
        foreach (var p in d.Parameters.Where(p => p.IsOutput))
            sb.Append(", ").Append(p.Name).Append(" AS ").Append(Quote(p.Name.TrimStart('@')));
        sb.Append(";\n");
        return sb.ToString();
    }

    public static string CreateTable(PlaygroundObjectDetail d)
    {
        var lines = new List<string>();
        foreach (var c in d.Columns)
        {
            if (c.ComputedDefinition is not null)
            {
                lines.Add($"{Quote(c.Name)} AS {c.ComputedDefinition}");
                continue;
            }
            var line = new StringBuilder(Quote(c.Name)).Append(' ').Append(c.Type);
            if (c.Collation is not null && IsTextType(c.Type)) line.Append(" COLLATE ").Append(c.Collation);
            if (c.IsIdentity) line.Append(" IDENTITY").Append(c.IdentityText ?? "(1,1)");
            line.Append(c.IsNullable ? " NULL" : " NOT NULL");
            if (c.DefaultDefinition is not null) line.Append(" DEFAULT ").Append(c.DefaultDefinition);
            lines.Add(line.ToString());
        }

        foreach (var i in d.Indexes.Where(i => i.IsPrimaryKey || i.IsUniqueConstraint))
        {
            var kind = i.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
            var cluster = i.Type.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase) ? "CLUSTERED " : "NONCLUSTERED ";
            lines.Add($"CONSTRAINT {Quote(i.Name)} {kind} {cluster}({KeyList(i.KeyColumns)})");
        }
        foreach (var fk in d.ForeignKeys)
        {
            var text = $"CONSTRAINT {Quote(fk.Name)} FOREIGN KEY ({string.Join(", ", fk.Columns.Select(Quote))}) " +
                       $"REFERENCES {QuoteObject(fk.ReferencedSchema, fk.ReferencedTable)} ({string.Join(", ", fk.ReferencedColumns.Select(Quote))})";
            if (Action(fk.OnDelete) is { } onDelete) text += " ON DELETE " + onDelete;
            if (Action(fk.OnUpdate) is { } onUpdate) text += " ON UPDATE " + onUpdate;
            lines.Add(text);
        }
        foreach (var check in d.Checks)
            lines.Add($"CONSTRAINT {Quote(check.Name)} CHECK {check.Definition}");

        var sb = new StringBuilder("CREATE TABLE ").Append(QuoteObject(d.Schema, d.Name)).Append("\n(\n");
        sb.Append(string.Join(",\n", lines.Select(l => "    " + l))).Append("\n);\n");

        foreach (var i in d.Indexes.Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint))
        {
            sb.Append("\nCREATE ").Append(i.IsUnique ? "UNIQUE " : "")
                .Append(i.Type.StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase) ? "CLUSTERED " : "NONCLUSTERED ")
                .Append("INDEX ").Append(Quote(i.Name)).Append(" ON ").Append(QuoteObject(d.Schema, d.Name))
                .Append(" (").Append(KeyList(i.KeyColumns)).Append(')');
            if (i.IncludedColumns.Count > 0) sb.Append(" INCLUDE (").Append(string.Join(", ", i.IncludedColumns.Select(Quote))).Append(')');
            if (i.Filter is not null) sb.Append(" WHERE ").Append(i.Filter);
            sb.Append(";\n");
        }
        return sb.ToString();
    }

    public static string AddColumn(PlaygroundObjectDetail d)
        => $"ALTER TABLE {QuoteObject(d.Schema, d.Name)}\n    ADD [NewColumn] nvarchar(100) NULL;\n";

    public static string Drop(PlaygroundObjectDetail d) => d.Kind switch
    {
        "TABLE" => $"DROP TABLE {QuoteObject(d.Schema, d.Name)};\n",
        "VIEW" => $"DROP VIEW {QuoteObject(d.Schema, d.Name)};\n",
        "PROCEDURE" => $"DROP PROCEDURE {QuoteObject(d.Schema, d.Name)};\n",
        _ => $"DROP FUNCTION {QuoteObject(d.Schema, d.Name)};\n",
    };

    [GeneratedRegex(@"\A((?:\s|--[^\r\n]*|/\*.*?\*/)*)CREATE(?!\s+OR\s+ALTER)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCreate();

    /// <summary>The stored definition with its leading CREATE turned into CREATE OR ALTER, so editing it and running it updates the object.</summary>
    public static string CreateOrAlter(string definition) => LeadingCreate().Replace(definition, "$1CREATE OR ALTER", 1);

    // ---- helpers ------------------------------------------------------------------------------

    private static IEnumerable<PlaygroundColumn> Writable(PlaygroundObjectDetail d)
        => d.Columns.Where(c => !c.IsIdentity && c.ComputedDefinition is null
            && !c.Type.Equals("timestamp", StringComparison.OrdinalIgnoreCase) && !c.Type.Equals("rowversion", StringComparison.OrdinalIgnoreCase));

    private static string Note(PlaygroundColumn c) => c.Type + (c.IsNullable ? "" : " NOT NULL");

    private static string WhereByKey(PlaygroundObjectDetail d)
    {
        var keys = d.Columns.Where(c => c.IsPrimaryKey).ToList();
        return keys.Count == 0
            ? "WHERE 1 = 0;  -- isi kondisi sebelum dijalankan\n"
            : "WHERE " + string.Join(" AND ", keys.Select(k => Quote(k.Name) + " = NULL")) + ";  -- isi nilai kunci sebelum dijalankan\n";
    }

    /// <summary>Items on their own lines with a trailing "-- note"; the comma goes before the comment.</summary>
    private static void AppendItems(StringBuilder sb, IReadOnlyList<(string Item, string Note)> items, string indent = "    ", string terminator = "")
    {
        for (var i = 0; i < items.Count; i++)
        {
            var last = i == items.Count - 1;
            sb.Append(indent).Append(items[i].Item).Append(last ? terminator : ",").Append("  -- ").Append(items[i].Note).Append('\n');
        }
    }

    private static string KeyList(IEnumerable<PlaygroundIndexColumn> columns)
        => string.Join(", ", columns.Select(c => Quote(c.Name) + (c.Descending ? " DESC" : "")));

    private static string? Action(string catalogName) => catalogName switch
    {
        "CASCADE" => "CASCADE",
        "SET_NULL" => "SET NULL",
        "SET_DEFAULT" => "SET DEFAULT",
        _ => null,
    };

    private static bool IsTextType(string type)
        => type.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase) || type.StartsWith("varchar", StringComparison.OrdinalIgnoreCase)
           || type.StartsWith("nchar", StringComparison.OrdinalIgnoreCase) || type.StartsWith("char", StringComparison.OrdinalIgnoreCase);

    // ---- new table ----------------------------------------------------------------------------

    /// <summary>
    /// CREATE TABLE text from the designer's form. Names and types are checked here (a bad one is an ArgumentException with a
    /// message for the builder); the schema is checked against the allowed schemas by the caller.
    /// </summary>
    public static string NewTable(NewTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        CheckIdentifier(spec.Schema, "Schema");
        CheckIdentifier(spec.Name, "Nama tabel");
        if (spec.Columns.Count == 0) throw new ArgumentException("Tabel butuh minimal satu kolom.");
        if (spec.Columns.Count > MaxColumns) throw new ArgumentException($"Maksimal {MaxColumns} kolom.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var primaryKey = new List<string>();
        var identities = 0;

        foreach (var c in spec.Columns)
        {
            CheckIdentifier(c.Name, "Nama kolom");
            if (!seen.Add(c.Name.Trim())) throw new ArgumentException($"Nama kolom '{c.Name.Trim()}' dipakai dua kali.");

            var type = (c.Type ?? "").Trim().ToLowerInvariant();
            var typeSql = ColumnType(c.Name.Trim(), type, c);
            var line = new StringBuilder(Quote(c.Name.Trim())).Append(' ').Append(typeSql);

            if (c.Identity)
            {
                if (type is not ("int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric") || (type is "decimal" or "numeric" && (c.Scale ?? 0) != 0))
                    throw new ArgumentException($"Kolom '{c.Name.Trim()}': IDENTITY hanya untuk tipe bilangan bulat.");
                if (++identities > 1) throw new ArgumentException("Satu tabel hanya boleh punya satu kolom IDENTITY.");
                if (!string.IsNullOrWhiteSpace(c.Default)) throw new ArgumentException($"Kolom '{c.Name.Trim()}': kolom IDENTITY tidak boleh punya nilai default.");
                line.Append(" IDENTITY(1,1)");
            }

            var notNull = c.PrimaryKey || c.Identity || !c.Nullable;
            line.Append(notNull ? " NOT NULL" : " NULL");
            if (!string.IsNullOrWhiteSpace(c.Default)) line.Append(" DEFAULT (").Append(DefaultLiteral(c.Name.Trim(), type, c.Default.Trim())).Append(')');
            if (c.PrimaryKey) primaryKey.Add(Quote(c.Name.Trim()));
            lines.Add(line.ToString());
        }

        if (primaryKey.Count > 0)
        {
            var constraint = "PK_" + spec.Name.Trim();
            if (constraint.Length > MaxIdentifierLength) constraint = constraint[..MaxIdentifierLength];
            lines.Add($"CONSTRAINT {Quote(constraint)} PRIMARY KEY CLUSTERED ({string.Join(", ", primaryKey)})");
        }

        return $"CREATE TABLE {QuoteObject(spec.Schema.Trim(), spec.Name.Trim())}\n(\n{string.Join(",\n", lines.Select(l => "    " + l))}\n);\n";
    }

    private static void CheckIdentifier(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"{what} wajib diisi.");
        var trimmed = name.Trim();
        if (trimmed.Length > MaxIdentifierLength) throw new ArgumentException($"{what} terlalu panjang (maks {MaxIdentifierLength} karakter).");
        if (trimmed.Any(char.IsControl)) throw new ArgumentException($"{what} mengandung karakter yang tidak boleh.");
    }

    private static string ColumnType(string column, string type, NewTableColumn c)
    {
        switch (type)
        {
            case "int" or "bigint" or "smallint" or "tinyint" or "bit" or "money" or "smallmoney" or "real"
                or "date" or "datetime" or "smalldatetime" or "uniqueidentifier" or "xml":
                return type;
            case "float":
                if (c.Precision is null) return "float";
                return InRange(column, "Presisi", c.Precision.Value, 1, 53) ? $"float({c.Precision})" : "";
            case "decimal" or "numeric":
                var p = c.Precision ?? 18;
                var s = c.Scale ?? 0;
                InRange(column, "Presisi", p, 1, 38);
                InRange(column, "Skala", s, 0, p);
                return $"{type}({p},{s})";
            case "datetime2" or "time" or "datetimeoffset":
                if (c.Scale is null) return type;
                InRange(column, "Skala", c.Scale.Value, 0, 7);
                return $"{type}({c.Scale})";
            case "varchar" or "nvarchar" or "varbinary":
                return $"{type}({Length(column, c.Length, type == "nvarchar" ? 4000 : 8000, allowMax: true, defaultLength: 100)})";
            case "char" or "nchar" or "binary":
                return $"{type}({Length(column, c.Length, type == "nchar" ? 4000 : 8000, allowMax: false, defaultLength: type == "binary" ? 16 : 10)})";
            default:
                throw new ArgumentException($"Kolom '{column}': tipe '{c.Type}' tidak didukung.");
        }
    }

    private static bool InRange(string column, string what, int value, int min, int max)
        => value >= min && value <= max ? true : throw new ArgumentException($"Kolom '{column}': {what} harus antara {min} dan {max}.");

    private static string Length(string column, string? text, int max, bool allowMax, int defaultLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultLength.ToString(CultureInfo.InvariantCulture);
        if (allowMax && text.Trim().Equals("max", StringComparison.OrdinalIgnoreCase)) return "max";
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 1 || length > max)
            throw new ArgumentException($"Kolom '{column}': panjang harus angka 1-{max}{(allowMax ? " atau max" : "")}.");
        return length.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly string[] DateFunctions = ["GETDATE()", "GETUTCDATE()", "SYSDATETIME()", "SYSUTCDATETIME()", "SYSDATETIMEOFFSET()", "CURRENT_TIMESTAMP"];
    private static readonly string[] GuidFunctions = ["NEWID()", "NEWSEQUENTIALID()"];

    /// <summary>A default is a number, a bit, a quoted text, or one of a few known functions; anything else is refused.</summary>
    private static string DefaultLiteral(string column, string type, string text)
    {
        var upper = text.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (type is "date" or "datetime" or "datetime2" or "smalldatetime" or "time" or "datetimeoffset" && DateFunctions.Contains(upper))
            return upper;
        if (type == "uniqueidentifier" && GuidFunctions.Contains(upper)) return upper;
        if (upper == "NULL") return "NULL";

        switch (type)
        {
            case "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real":
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    throw new ArgumentException($"Kolom '{column}': default '{text}' bukan angka.");
                return text.Trim();
            case "bit":
                return upper switch
                {
                    "1" or "TRUE" => "1",
                    "0" or "FALSE" => "0",
                    _ => throw new ArgumentException($"Kolom '{column}': default bit harus 0/1 atau true/false."),
                };
            case "nvarchar" or "nchar":
                return "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
            case "varchar" or "char" or "date" or "datetime" or "datetime2" or "smalldatetime" or "time" or "datetimeoffset":
                return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
            case "uniqueidentifier":
                if (!Guid.TryParse(text, out var guid)) throw new ArgumentException($"Kolom '{column}': default bukan GUID yang valid.");
                return "'" + guid.ToString("D").ToUpperInvariant() + "'";
            default:
                throw new ArgumentException($"Kolom '{column}': default tidak didukung untuk tipe {type}.");
        }
    }
}
