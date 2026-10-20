using System.Text;
using System.Text.RegularExpressions;

namespace Rakit.Core.Playground;

/// <summary>One batch of a script. <see cref="StartLine"/> is the 1-based line of the whole script where the batch's text begins.</summary>
public sealed record SqlBatch(string Sql, int StartLine);

public sealed class SqlBatchException(string message, int line) : ArgumentException(message)
{
    public int Line { get; } = line;
}

/// <summary>
/// Splits a script into batches at lines that hold only <c>GO</c> (as SSMS does). A <c>GO</c> inside a block comment, a
/// string or a quoted name is ordinary text. <c>GO n</c> (repeat) is refused rather than silently run once.
/// </summary>
public static partial class SqlBatchSplitter
{
    [GeneratedRegex(@"^\s*GO\s*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^\s*GO\s+\d+\s*(--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoCountLine();

    public static IReadOnlyList<SqlBatch> Split(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var batches = new List<SqlBatch>();
        var current = new StringBuilder();
        var startLine = 1;
        var scan = new ScanState();
        var lineNumber = 0;

        void Flush()
        {
            var text = current.ToString();
            if (text.AsSpan().Trim().Length > 0) batches.Add(new SqlBatch(text, startLine));
            current.Clear();
        }

        foreach (var (line, terminator) in Lines(script))
        {
            lineNumber++;
            if (scan.IsPlain)
            {
                if (GoLine().IsMatch(line))
                {
                    Flush();
                    continue;
                }
                if (GoCountLine().IsMatch(line))
                    throw new SqlBatchException("'GO n' (ulang n kali) tidak didukung. Tulis 'GO' saja, atau ulangi perintahnya.", lineNumber);
            }

            if (current.Length == 0) startLine = lineNumber;
            current.Append(line).Append(terminator);
            scan.Advance(line);
        }
        Flush();
        return batches;
    }

    private static IEnumerable<(string Line, string Terminator)> Lines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            var length = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            yield return (text[start..i], text.Substring(i, length));
            i += length - 1;
            start = i + 1;
        }
        if (start < text.Length) yield return (text[start..], "");
    }

    /// <summary>Tracks whether the end of a line is inside a block comment (these nest in T-SQL), a string or a quoted name.</summary>
    private sealed class ScanState
    {
        private int commentDepth;
        private bool inString, inBracket, inQuote;

        public bool IsPlain => commentDepth == 0 && !inString && !inBracket && !inQuote;

        public void Advance(string line)
        {
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';

                if (inString) { if (c == '\'') { if (next == '\'') i++; else inString = false; } continue; }
                if (inBracket) { if (c == ']') { if (next == ']') i++; else inBracket = false; } continue; }
                if (inQuote) { if (c == '"') { if (next == '"') i++; else inQuote = false; } continue; }
                if (commentDepth > 0)
                {
                    if (c == '*' && next == '/') { commentDepth--; i++; }
                    else if (c == '/' && next == '*') { commentDepth++; i++; }
                    continue;
                }

                if (c == '-' && next == '-') return;                       // the rest of the line is a comment
                if (c == '/' && next == '*') { commentDepth++; i++; }
                else if (c == '\'') inString = true;
                else if (c == '[') inBracket = true;
                else if (c == '"') inQuote = true;
            }
        }
    }
}
