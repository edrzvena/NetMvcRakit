using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Rakit.Core.Data;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;
using StatementType = Rakit.Core.DataSources.StatementType;

namespace Rakit.Core.Sql;

public sealed class SqlExecutionService(
    IDbConnectionFactory connections, ISchemaBrowserService schemaBrowser, ILogger<SqlExecutionService> logger)
    : ISqlExecutionService
{
    private const int SqlTimeoutErrorNumber = -2;
    private const int FirstUserErrorNumber = 50000;
    private const string ReturnValueParameter = "@RETURN_VALUE";

    public async Task<SqlExecResult> ExecuteQueryAsync(
        string sqlTemplate, IDictionary<string, object?> args, SqlExecOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlTemplate);
        ArgumentNullException.ThrowIfNull(args);
        ValidateOptions(options);

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

    public async Task<SqlExecResult> ExecuteProcedureAsync(
        string schema, string name, IDictionary<string, object?> args, SqlExecOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(args);
        ValidateOptions(options);

        var declared = await schemaBrowser.GetProcedureParametersAsync(schema, name, ct);
        var values = args.ToDictionary(kv => kv.Key.TrimStart('@'), kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var conn = await connections.CreateExecutionAsync(ct);
            await using var cmd = conn.CreateCommand();
            // Names come from the catalog (already validated against AllowedSchemas); the call is never built by EXEC.
            cmd.CommandText = $"[{schema.Trim().Replace("]", "]]")}].[{name.Trim().Replace("]", "]]")}]";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandTimeout = options.TimeoutSeconds;

            var returnValue = new SqlParameter(ReturnValueParameter, SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            cmd.Parameters.Add(returnValue);

            var outputs = new List<SqlParameter>();
            foreach (var declaredParameter in declared)
            {
                var bound = BindParameter(declaredParameter, values);
                if (bound is null) continue;
                cmd.Parameters.Add(bound);
                if (bound.Direction != ParameterDirection.Input) outputs.Add(bound);
            }

            await using var tx = options.RollbackAfter || options.CommitOnlyIfReturnValueZero
                ? (SqlTransaction)await conn.BeginTransactionAsync(ct) : null;
            cmd.Transaction = tx;

            var read = await ReadResultSetsAsync(cmd, options.MaxRows, ct);

            // Output values only exist once the reader is closed, and not at all when the command was cancelled.
            var outputValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in outputs)
                outputValues[output.ParameterName.TrimStart('@')] =
                    read.Truncated || output.Value is DBNull or null ? null : output.Value;
            int? returned = read.Truncated || returnValue.Value is DBNull or null ? null : Convert.ToInt32(returnValue.Value, CultureInfo.InvariantCulture);

            if (tx is not null)
            {
                // A cut-short read (return value unknown) is never committed.
                if (!options.RollbackAfter && returned == 0) await tx.CommitAsync(ct);
                else await RollbackQuietlyAsync(tx);
            }

            return new SqlExecResult(read.ResultSets, outputValues, returned, Math.Max(read.RecordsAffected, 0), stopwatch.Elapsed);
        }
        catch (SqlException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw Translate(ex, options.TimeoutSeconds);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // SqlClient converts values to the declared parameter type when sending; a value that cannot be converted lands here.
            throw new ArgumentException("Salah satu nilai parameter tidak cocok dengan tipe parameter prosedur: " + ex.Message, nameof(args), ex);
        }
    }

    private static void ValidateOptions(SqlExecOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TimeoutSeconds is < 1 or > DataSourceDefinition.MaxTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(options), $"Timeout harus antara 1 dan {DataSourceDefinition.MaxTimeoutSeconds} detik.");
        if (options.MaxRows is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRows harus lebih dari 0.");
    }

    private static async Task<SqlExecResult> RunCommandAsync(SqlCommand cmd, Stopwatch stopwatch, CancellationToken ct)
    {
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return new SqlExecResult([], new Dictionary<string, object?>(), null, Math.Max(affected, 0), stopwatch.Elapsed);
    }

    private static async Task<SqlExecResult> RunQueryAsync(
        SqlCommand cmd, int? maxRows, Stopwatch stopwatch, CancellationToken ct)
    {
        var read = await ReadResultSetsAsync(cmd, maxRows, ct);
        return new SqlExecResult(read.ResultSets, new Dictionary<string, object?>(), null, Math.Max(read.RecordsAffected, 0), stopwatch.Elapsed);
    }

    private sealed record ReadOutcome(List<DataTableResult> ResultSets, int RecordsAffected, bool Truncated);

    /// <summary>Reads every result set, each capped at <paramref name="maxRows"/>; a capped set cancels the command and ends the read.</summary>
    private static async Task<ReadOutcome> ReadResultSetsAsync(SqlCommand cmd, int? maxRows, CancellationToken ct)
    {
        var resultSets = new List<DataTableResult>();
        var anyTruncated = false;
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
                    anyTruncated = true;
                    try { cmd.Cancel(); } catch (SqlException) { /* already finished */ }
                    break;
                }
            }
            while (await reader.NextResultAsync(ct));

            recordsAffected = reader.RecordsAffected;
        }

        return new ReadOutcome(resultSets, recordsAffected, anyTruncated);
    }

    // ---- stored procedure parameters -------------------------------------------------------------

    /// <summary>Null = leave the parameter out (optional input without a value).</summary>
    private static SqlParameter? BindParameter(ProcedureParameter declared, Dictionary<string, object?> values)
    {
        var plainName = declared.Name.TrimStart('@');

        var supplied = values.TryGetValue(plainName, out var value);
        if (!declared.IsOutput && !supplied)
        {
            if (declared.HasDefault) return null;
            throw new ArgumentException($"Nilai untuk parameter '{plainName}' tidak diberikan.", "args");
        }

        // Checked only now so an unsupported type on an optional parameter that is left out does not block the call.
        var type = ToSqlDbType(declared.DataType)
            ?? throw new ArgumentException($"Parameter '{plainName}' bertipe '{declared.DataType}', yang tidak didukung.", "args");

        var parameter = new SqlParameter(declared.Name, type)
        {
            // An OUTPUT parameter is always sent so its value can be read back; one that also gets a value is InputOutput.
            Direction = !declared.IsOutput ? ParameterDirection.Input
                : supplied ? ParameterDirection.InputOutput : ParameterDirection.Output,
        };

        switch (type)
        {
            case SqlDbType.Char or SqlDbType.VarChar or SqlDbType.Binary or SqlDbType.VarBinary:
                parameter.Size = declared.MaxLength;
                break;
            case SqlDbType.NChar or SqlDbType.NVarChar:
                parameter.Size = declared.MaxLength < 0 ? -1 : declared.MaxLength / 2;
                break;
            case SqlDbType.Decimal:
                parameter.Precision = declared.Precision;
                parameter.Scale = declared.Scale;
                break;
            case SqlDbType.DateTime2 or SqlDbType.DateTimeOffset or SqlDbType.Time:
                parameter.Scale = declared.Scale;
                break;
        }

        if (supplied)
        {
            var dbValue = ToDbValue(plainName, value);
            if (dbValue is string text) dbValue = CoerceString(plainName, text, type);
            EnsureFits(plainName, dbValue, parameter);
            parameter.Value = dbValue;
        }
        else
        {
            parameter.Value = DBNull.Value;
        }
        return parameter;
    }

    /// <summary>
    /// Values from query strings and forms arrive as text, but a typed parameter will not convert them itself
    /// (string to uniqueidentifier fails). Text for a non-text parameter is parsed with the invariant culture;
    /// blank text means "no value" (null) rather than the 0 / 1900-01-01 SQL Server would make of ''.
    /// </summary>
    private static object CoerceString(string name, string text, SqlDbType type)
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
                SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney => decimal.Parse(trimmed, NumberStyles.Number, culture),
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

    /// <summary>SqlClient silently cuts an over-long value to the parameter size; SQL Server itself would raise an error, so do the same.</summary>
    private static void EnsureFits(string name, object dbValue, SqlParameter parameter)
    {
        if (parameter.Size <= 0) return;
        var length = dbValue switch { string s => s.Length, byte[] b => b.Length, _ => -1 };
        if (length > parameter.Size)
            throw new ArgumentException($"Nilai parameter '{name}' melebihi panjang maksimum {parameter.Size}.", "args");
    }

    private static SqlDbType? ToSqlDbType(string typeName) => typeName.ToLowerInvariant() switch
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

    private static object ToDbValue(string name, object? value) => value switch
    {
        null => DBNull.Value,
        string or bool or byte or short or int or long or float or double or decimal
            or DateTime or DateTimeOffset or TimeSpan or Guid or byte[] => value,
        _ => throw new ArgumentException(
            $"Nilai parameter '{name}' bertipe {value.GetType().Name}, yang tidak didukung.", "args"),
    };

    private static async Task RollbackQuietlyAsync(SqlTransaction tx)
    {
        try { await tx.RollbackAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or SqlException)
        {
            // The procedure ended the transaction itself; disposing the connection rolls back whatever is left.
        }
    }

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
