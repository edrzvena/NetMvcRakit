using System.Text;
using Rakit.Core.Playground;

namespace Rakit.Core.Modules;

/// <summary>
/// CREATE OR ALTER text of the CRUD procedures the table wizard makes. They follow the sample convention (RETURN 0 = ok,
/// 1 = not found; a business error is a THROW) and carry <see cref="Marker"/> so the wizard can tell its own procedures from
/// ones a person wrote. Names come from the catalog and are quoted with [ ]; nothing here runs SQL.
/// </summary>
public static class TableProcedureScripts
{
    /// <summary>Text inside the body of every generated procedure. A procedure with it may be regenerated; one without is never replaced.</summary>
    public const string Marker = "rakit:generated";

    /// <summary>The output parameter that returns an identity key from Insert.</summary>
    public const string NewIdParameter = "NewId";

    private static string Q(string name) => PlaygroundScripts.Quote(name);

    private static string Param(PlaygroundColumn c, bool required)
        => "@" + c.Name + " " + c.Type + (required ? "" : " = NULL");

    /// <summary>
    /// A default that SQL Server only accepts as a DEFAULT constraint (NEWSEQUENTIALID()) or not inside ISNULL (NEXT VALUE FOR):
    /// such a column is left out of a generated INSERT so the database fills it.
    /// </summary>
    public static bool HasDefaultOnlyValue(PlaygroundColumn c)
        => c.DefaultDefinition is { } d
           && (d.Contains("newsequentialid", StringComparison.OrdinalIgnoreCase) || d.Contains("next value for", StringComparison.OrdinalIgnoreCase));

    /// <summary>NOT NULL without a database default has to be given; everything else may be left out.</summary>
    private static bool IsRequired(PlaygroundColumn c) => !c.IsNullable && c.DefaultDefinition is null;

    private static string Header(string schema, string proc, string table, string operation, IEnumerable<string> parameters)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE OR ALTER PROCEDURE ").Append(PlaygroundScripts.QuoteObject(schema, proc)).Append('\n');
        sb.Append(string.Join(",\n", parameters.Select(p => "    " + p))).Append('\n');
        sb.Append("AS\nBEGIN\n");
        sb.Append("    -- ").Append(Marker).Append(": ").Append(operation).Append(" for ").Append(PlaygroundScripts.QuoteObject(schema, table))
            .Append(". Made by the Rakit table wizard; generating it again replaces this procedure.\n");
        sb.Append("    SET NOCOUNT ON;\n\n");
        return sb.ToString();
    }

    /// <summary><paramref name="columns"/> are the writable columns in table order; <paramref name="identityKey"/> is the identity primary key to return as @NewId, if any.</summary>
    public static string Insert(string schema, string table, string proc, IReadOnlyList<PlaygroundColumn> columns, PlaygroundColumn? identityKey)
    {
        var parameters = columns.Select(c => Param(c, IsRequired(c))).ToList();
        if (identityKey is not null) parameters.Add($"@{NewIdParameter} {identityKey.Type} = NULL OUTPUT");

        var sb = new StringBuilder(Header(schema, proc, table, "Insert", parameters));
        if (columns.Count == 0)
        {
            sb.Append("    INSERT INTO ").Append(PlaygroundScripts.QuoteObject(schema, table)).Append(" DEFAULT VALUES;\n");
        }
        else
        {
            sb.Append("    INSERT INTO ").Append(PlaygroundScripts.QuoteObject(schema, table))
                .Append(" (").Append(string.Join(", ", columns.Select(c => Q(c.Name)))).Append(")\n");
            sb.Append("    VALUES (").Append(string.Join(", ", columns.Select(c =>
                !c.IsNullable && c.DefaultDefinition is not null ? $"ISNULL(@{c.Name}, {c.DefaultDefinition})" : "@" + c.Name))).Append(");\n");
        }
        if (identityKey is not null)
            sb.Append("\n    SET @").Append(NewIdParameter).Append(" = CONVERT(").Append(identityKey.Type).Append(", SCOPE_IDENTITY());\n");
        sb.Append("\n    RETURN 0;\nEND");
        return sb.ToString();
    }

    /// <summary><paramref name="columns"/> are the writable columns without the key; returns null when there is nothing to update.</summary>
    public static string? Update(string schema, string table, string proc, PlaygroundColumn key, IReadOnlyList<PlaygroundColumn> columns)
    {
        if (columns.Count == 0) return null;

        var parameters = new List<string> { Param(key, true) };
        parameters.AddRange(columns.Select(c => Param(c, IsRequired(c))));

        var sb = new StringBuilder(Header(schema, proc, table, "Update", parameters));
        var target = PlaygroundScripts.QuoteObject(schema, table);
        sb.Append("    IF NOT EXISTS (SELECT 1 FROM ").Append(target).Append(" WHERE ").Append(Q(key.Name)).Append(" = @").Append(key.Name).Append(")\n");
        sb.Append("        RETURN 1;\n\n");
        sb.Append("    UPDATE ").Append(target).Append("\n    SET ");
        sb.Append(string.Join(",\n        ", columns.Select(c =>
            !c.IsNullable && c.DefaultDefinition is not null ? $"{Q(c.Name)} = ISNULL(@{c.Name}, {Q(c.Name)})" : $"{Q(c.Name)} = @{c.Name}")));
        sb.Append("\n    WHERE ").Append(Q(key.Name)).Append(" = @").Append(key.Name).Append(";\n");
        sb.Append("\n    RETURN 0;\nEND");
        return sb.ToString();
    }

    public static string Delete(string schema, string table, string proc, PlaygroundColumn key)
    {
        var sb = new StringBuilder(Header(schema, proc, table, "Delete", [Param(key, true)]));
        var target = PlaygroundScripts.QuoteObject(schema, table);
        sb.Append("    IF NOT EXISTS (SELECT 1 FROM ").Append(target).Append(" WHERE ").Append(Q(key.Name)).Append(" = @").Append(key.Name).Append(")\n");
        sb.Append("        RETURN 1;\n\n");
        sb.Append("    DELETE FROM ").Append(target).Append(" WHERE ").Append(Q(key.Name)).Append(" = @").Append(key.Name).Append(";\n");
        sb.Append("\n    RETURN 0;\nEND");
        return sb.ToString();
    }

    /// <summary>True when the stored definition of a procedure was made by the wizard.</summary>
    public static bool IsGenerated(string? definition)
        => definition is not null && definition.Contains(Marker, StringComparison.Ordinal);
}
