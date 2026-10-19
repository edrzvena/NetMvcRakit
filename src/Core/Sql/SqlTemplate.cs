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
