using System.Text;
using System.Text.RegularExpressions;

namespace Rakit.Core.Sql;

public sealed class SqlTemplateException(string message) : Exception(message);

/// <param name="Sql">The template with every <c>{name}</c> rewritten to <c>@name</c>.</param>
/// <param name="Placeholders">Distinct <c>{name}</c> placeholders, in order of first appearance.</param>
public sealed record SqlTemplateResult(string Sql, IReadOnlyList<string> Placeholders);

public static partial class SqlTemplate
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"(?<![@\w])@([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex AtNamePattern();

    /// <summary>
    /// Rewrites <c>{name}</c> to <c>@name</c> outside string literals and comments. A placeholder inside a
    /// string literal is rejected: parameters are bound, so they must not be wrapped in quotes.
    /// </summary>
    /// <exception cref="SqlTemplateException"/>
    public static SqlTemplateResult Rewrite(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var sql = new StringBuilder(template.Length);
        var placeholders = new List<string>();
        var i = 0;

        while (i < template.Length)
        {
            var c = template[i];

            if (c == '\'')
            {
                var end = EndOfStringLiteral(template, i);
                ThrowIfPlaceholderInside(template, i, end);
                sql.Append(template, i, end - i);
                i = end;
            }
            else if (c == '-' && Peek(template, i + 1) == '-')
            {
                var end = template.IndexOf('\n', i);
                end = end < 0 ? template.Length : end;
                sql.Append(template, i, end - i);
                i = end;
            }
            else if (c == '/' && Peek(template, i + 1) == '*')
            {
                var end = template.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? template.Length : end + 2;
                sql.Append(template, i, end - i);
                i = end;
            }
            else if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                var name = close < 0 ? null : template[(i + 1)..close];
                if (name is null || !NamePattern().IsMatch(name))
                    throw new SqlTemplateException(
                        $"Placeholder pada posisi {i} tidak valid. Gunakan {{namaParameter}} (huruf/angka/_, tidak diawali angka).");

                if (!placeholders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    placeholders.Add(name);
                sql.Append('@').Append(name);
                i = close + 1;
            }
            else
            {
                sql.Append(c);
                i++;
            }
        }

        return new SqlTemplateResult(sql.ToString(), placeholders);
    }

    /// <summary>Distinct <c>@name</c> tokens in already-rewritten SQL (also finds native ones such as @Skip).</summary>
    public static IReadOnlyList<string> ReferencedParameters(string sql)
        => AtNamePattern().Matches(sql)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [GeneratedRegex(@"\bDECLARE\b([^;\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex DeclarePattern();

    [GeneratedRegex(@"^\s*@([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DeclaredNamePattern();

    /// <summary>
    /// Local variables the SQL declares itself (<c>DECLARE @x int, @y int</c>, on the line of the DECLARE), outside string
    /// literals and comments. Such a name is the SQL's own variable: binding a parameter of that name as well fails with
    /// "The variable name has already been declared". Only a comma outside parentheses starts the next declaration, so the
    /// <c>@b</c> of <c>DECLARE @x int = COALESCE(@a, @b)</c> stays a parameter.
    /// </summary>
    public static IReadOnlyList<string> DeclaredVariables(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var code = WithoutLiteralsAndComments(sql);
        return DeclarePattern().Matches(code)
            .SelectMany(m => TopLevelParts(m.Groups[1].Value))
            .Select(part => DeclaredNamePattern().Match(part))
            .Where(n => n.Success)
            .Select(n => n.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The text split at the commas that are not inside parentheses.</summary>
    private static IEnumerable<string> TopLevelParts(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && depth > 0) depth--;
            else if (text[i] == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>The text with string literals and comments blanked out (same length, line breaks kept).</summary>
    private static string WithoutLiteralsAndComments(string s)
    {
        var chars = s.ToCharArray();
        void Blank(int from, int to)
        {
            for (var k = from; k < to && k < chars.Length; k++)
                if (chars[k] is not ('\n' or '\r')) chars[k] = ' ';
        }

        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\'')
            {
                var end = EndOfStringLiteral(s, i);
                Blank(i, end);
                i = end;
            }
            else if (s[i] == '-' && Peek(s, i + 1) == '-')
            {
                var end = s.IndexOf('\n', i);
                end = end < 0 ? s.Length : end;
                Blank(i, end);
                i = end;
            }
            else if (s[i] == '/' && Peek(s, i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? s.Length : end + 2;
                Blank(i, end);
                i = end;
            }
            else
            {
                i++;
            }
        }
        return new string(chars);
    }

    private static char Peek(string s, int index) => index < s.Length ? s[index] : '\0';

    /// <summary>Index just past the closing quote ('' is an escaped quote).</summary>
    private static int EndOfStringLiteral(string s, int start)
    {
        var i = start + 1;
        while (i < s.Length)
        {
            if (s[i] == '\'')
            {
                if (Peek(s, i + 1) == '\'') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static void ThrowIfPlaceholderInside(string s, int start, int end)
    {
        var literal = s.AsSpan(start, end - start);
        var open = literal.IndexOf('{');
        if (open < 0) return;

        var rest = literal[(open + 1)..];
        var close = rest.IndexOf('}');
        if (close > 0 && NamePattern().IsMatch(rest[..close]))
            throw new SqlTemplateException(
                $"Placeholder {{{rest[..close]}}} berada di dalam tanda kutip. Hapus tanda kutipnya: parameter otomatis ter-binding.");
    }
}
