using System.Globalization;
using System.Text;

namespace Rakit.Core.Modules;

/// <summary>
/// CSV (RFC 4180 quoting) for the export and import of a Table module. Reading accepts a BOM, CRLF / LF / CR line ends and a comma,
/// semicolon or tab delimiter (taken from the header line, so a file saved by an Indonesian-locale Excel works); writing always
/// uses commas and CRLF.
/// </summary>
public static class ModuleCsv
{
    /// <summary>Rows of cells; fully blank lines are dropped. <paramref name="delimiter"/> is the one that was detected.</summary>
    public static List<List<string>> Parse(string text, out char delimiter)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        delimiter = DetectDelimiter(text);

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;      // inside "..."
        var cellStarted = false; // the cell had any character (a quote counts), so "" is an empty cell and not "no cell"

        void EndCell() { row.Add(cell.ToString()); cell.Clear(); cellStarted = false; }
        void EndRow()
        {
            if (cellStarted || cell.Length > 0 || row.Count > 0) EndCell();
            if (row.Any(c => c.Trim().Length > 0)) rows.Add(row);
            row = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }

            if (c == '"' && cell.Length == 0 && !cellStarted) { quoted = true; cellStarted = true; }
            else if (c == delimiter) EndCell();
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
            }
            else { cell.Append(c); cellStarted = true; }
        }
        EndRow();
        return rows;
    }

    /// <summary>The delimiter that occurs most in the first line outside quotes (comma when there is none).</summary>
    private static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '\r' or '\n') break;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.OrderByDescending(kv => kv.Value).First();
        return best.Value > 0 ? best.Key : ',';
    }

    /// <summary>One CSV line per row (CRLF), cells quoted only when they have to be.</summary>
    public static string Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, header);
        foreach (var row in rows) AppendRow(sb, row);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string?> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Quote(cells[i]));
        }
        sb.Append("\r\n");
    }

    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needs = value.Any(c => c is ',' or '"' or '\r' or '\n') || value[0] == ' ' || value[^1] == ' ';
        return needs ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>
    /// A text cell that a spreadsheet would read as a formula (starts with =, +, -, @, tab or CR) gets a leading apostrophe, so
    /// opening an export cannot run anything.
    /// </summary>
    public static string GuardFormula(string text)
        => text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    /// <summary>A database value as a CSV cell, by the column's format: invariant numbers, ISO dates, true / false.</summary>
    public static string Cell(object? value, ColumnFormat format)
    {
        switch (value)
        {
            case null:
                return "";
            case DateTime d:
                return d.ToString(format == ColumnFormat.Date ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case DateTimeOffset d:
                return d.ToString(format == ColumnFormat.Date ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case bool b:
                return b ? "true" : "false";
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double:
                return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
            default:
                return GuardFormula(ModuleCellFormatter.Plain(value) ?? "");
        }
    }
}
