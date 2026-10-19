using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Rakit.Core.Data;
using Rakit.Core.DataSources;

namespace Rakit.Core.Sql;

public sealed class SqlExecutionService(IDbConnectionFactory connections, ILogger<SqlExecutionService> logger)
    : ISqlExecutionService
{
    private const int SqlTimeoutErrorNumber = -2;
    private const int FirstUserErrorNumber = 50000;

    public async Task<SqlExecResult> ExecuteQueryAsync(
        string sqlTemplate, IDictionary<string, object?> args, SqlExecOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlTemplate);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        if (options.TimeoutSeconds is < 1 or > DataSourceDefinition.MaxTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(options), $"Timeout harus antara 1 dan {DataSourceDefinition.MaxTimeoutSeconds} detik.");
        if (options.MaxRows is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRows harus lebih dari 0.");

        var template = SqlTemplate.Rewrite(sqlTemplate);
        var values = args.ToDictionary(kv => kv.Key.TrimStart('@'), kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var placeholder in template.Placeholders)
            if (!values.ContainsKey(placeholder))
                throw new ArgumentException($"Nilai untuk parameter '{placeholder}' tidak diberikan.", nameof(args));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var conn = await connections.CreateExecutionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = template.Sql;
            cmd.CommandTimeout = options.TimeoutSeconds;

            // Only parameters the SQL actually mentions are sent; unused arguments are ignored.
            foreach (var name in SqlTemplate.ReferencedParameters(template.Sql))
                if (values.TryGetValue(name, out var value))
                    cmd.Parameters.Add(new SqlParameter("@" + name, ToDbValue(name, value)));

            return options.StatementType == StatementType.Command
                ? await RunCommandAsync(cmd, stopwatch, ct)
                : await RunQueryAsync(cmd, options.MaxRows, stopwatch, ct);
        }
        catch (SqlException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw Translate(ex, options.TimeoutSeconds);
        }
    }

    private static async Task<SqlExecResult> RunCommandAsync(SqlCommand cmd, Stopwatch stopwatch, CancellationToken ct)
    {
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return new SqlExecResult([], new Dictionary<string, object?>(), null, Math.Max(affected, 0), stopwatch.Elapsed);
    }

    private static async Task<SqlExecResult> RunQueryAsync(
        SqlCommand cmd, int? maxRows, Stopwatch stopwatch, CancellationToken ct)
    {
        var resultSets = new List<DataTableResult>();
        int recordsAffected;

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            do
            {
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<IReadOnlyList<object?>>();
                var truncated = false;

                while (await reader.ReadAsync(ct))
                {
                    if (maxRows is not null && rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }

                resultSets.Add(new DataTableResult(columns, rows, truncated));

                if (truncated)
                {
                    // Stop the server from streaming the remainder; later result sets are skipped as well.
                    try { cmd.Cancel(); } catch (SqlException) { /* already finished */ }
                    break;
                }
            }
            while (await reader.NextResultAsync(ct));

            recordsAffected = reader.RecordsAffected;
        }

        return new SqlExecResult(resultSets, new Dictionary<string, object?>(), null, Math.Max(recordsAffected, 0), stopwatch.Elapsed);
    }

    private static object ToDbValue(string name, object? value) => value switch
    {
        null => DBNull.Value,
        string or bool or byte or short or int or long or float or double or decimal
            or DateTime or DateTimeOffset or TimeSpan or Guid or byte[] => value,
        _ => throw new ArgumentException(
            $"Nilai parameter '{name}' bertipe {value.GetType().Name}, yang tidak didukung.", "args"),
    };

    private SqlExecutionException Translate(SqlException ex, int timeoutSeconds)
    {
        if (ex.Number == SqlTimeoutErrorNumber)
            return new SqlExecutionException(
                SqlExecErrorKind.Timeout,
                string.Create(CultureInfo.InvariantCulture, $"Query melebihi batas waktu {timeoutSeconds} detik."),
                ex.Message, ex);

        if (ex.Number >= FirstUserErrorNumber && ex.Class is >= 11 and <= 16)
            return new SqlExecutionException(SqlExecErrorKind.Business, ex.Message, ex.Message, ex);

        logger.LogError(ex, "SQL execution failed (error {Number}, severity {Class}).", ex.Number, ex.Class);
        return new SqlExecutionException(
            SqlExecErrorKind.Database, "Terjadi kesalahan saat menjalankan query.", ex.Message, ex);
    }
}
