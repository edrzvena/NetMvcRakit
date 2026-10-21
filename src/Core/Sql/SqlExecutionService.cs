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
    private const string ReadSavepoint = "rakit_read";

    /// <summary>The scope's connection and transaction; the owner commits, rolls back and disposes them.</summary>
    private sealed class SqlScope(SqlConnection connection, SqlTransaction transaction) : ISqlScope
    {
        private bool completed;

        public SqlConnection Connection { get; } = connection;
        public SqlTransaction Transaction { get; } = transaction;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (completed) throw new InvalidOperationException("The scope is already completed.");
            completed = true;
            await Transaction.CommitAsync(ct);
        }

        public async Task RollbackAsync()
        {
            if (completed) return;
            completed = true;
            await RollbackQuietlyAsync(Transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await RollbackAsync();
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    public async Task<ISqlScope> BeginScopeAsync(CancellationToken ct = default)
    {
        try
        {
            var conn = await connections.CreateExecutionAsync(ct);
            try
            {
                return new SqlScope(conn, (SqlTransaction)await conn.BeginTransactionAsync(ct));
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }
        catch (SqlException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw Translate(ex, DataSourceDefinition.DefaultTimeoutSeconds);
        }
    }

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
            var scope = options.Scope as SqlScope;
            await using var owned = scope is null ? await connections.CreateExecutionAsync(ct) : null;
            var conn = scope?.Connection ?? owned!;

            // RollbackAfter: whatever the statement changed is undone once it has been read (its own transaction, or a
            // savepoint of the scope's). SQL that only "queries" can still write: rakit_exec may execute every procedure
            // of the app schema, and a procedure name alone at the start of a batch runs it.
            await using var tx = options.RollbackAfter && scope is null ? (SqlTransaction)await conn.BeginTransactionAsync(ct) : null;
            var savepoint = options.RollbackAfter && scope is not null;
            if (savepoint) scope!.Transaction.Save(ReadSavepoint);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = scope?.Transaction ?? tx;
            cmd.CommandText = template.Sql;
            cmd.CommandTimeout = options.TimeoutSeconds;

            // Only parameters the SQL actually mentions are sent; unused arguments are ignored, and so is a name the SQL
            // declares as its own variable (DECLARE @Take ...), unless it is a {placeholder}.
            var declared = SqlTemplate.DeclaredVariables(template.Sql)
                .Where(d => !template.Placeholders.Contains(d, StringComparer.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in SqlTemplate.ReferencedParameters(template.Sql))
                if (!declared.Contains(name) && values.TryGetValue(name, out var value))
                    cmd.Parameters.Add(new SqlParameter("@" + name, ToDbValue(name, value)));

            try
            {
                return options.StatementType == StatementType.Command
                    ? await RunCommandAsync(cmd, stopwatch, ct)
                    : await RunQueryAsync(cmd, options.MaxRows, stopwatch, ct);
            }
            finally
            {
                if (tx is not null) await RollbackQuietlyAsync(tx);
                else if (savepoint) RollbackToSavepointQuietly(scope!.Transaction);
            }
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
            var scope = options.Scope as SqlScope;
            await using var owned = scope is null ? await connections.CreateExecutionAsync(ct) : null;
            var conn = scope?.Connection ?? owned!;
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

            // With a scope the owner decides about the transaction; otherwise a preview / commit-if-zero call has its own.
            await using var tx = scope is null && (options.RollbackAfter || options.CommitOnlyIfReturnValueZero)
                ? (SqlTransaction)await conn.BeginTransactionAsync(ct) : null;
            cmd.Transaction = scope?.Transaction ?? tx;

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
        var type = SqlValues.ToSqlDbType(declared.DataType)
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
            if (dbValue is string text) dbValue = SqlValues.FromText(plainName, text, type);
            EnsureFits(plainName, dbValue, parameter);
            parameter.Value = dbValue;
        }
        else
        {
            parameter.Value = DBNull.Value;
        }
        return parameter;
    }

    /// <summary>SqlClient silently cuts an over-long value to the parameter size; SQL Server itself would raise an error, so do the same.</summary>
    private static void EnsureFits(string name, object dbValue, SqlParameter parameter)
    {
        if (parameter.Size <= 0) return;
        var length = dbValue switch { string s => s.Length, byte[] b => b.Length, _ => -1 };
        if (length > parameter.Size)
            throw new ArgumentException($"Nilai parameter '{name}' melebihi panjang maksimum {parameter.Size}.", "args");
    }

    private static object ToDbValue(string name, object? value) => value switch
    {
        null => DBNull.Value,
        string or bool or byte or short or int or long or float or double or decimal
            or DateTime or DateTimeOffset or TimeSpan or Guid or byte[] => value,
        _ => throw new ArgumentException(
            $"Nilai parameter '{name}' bertipe {value.GetType().Name}, yang tidak didukung.", "args"),
    };

    private static void RollbackToSavepointQuietly(SqlTransaction tx)
    {
        try { tx.Rollback(ReadSavepoint); }
        catch (Exception ex) when (ex is InvalidOperationException or SqlException)
        {
            // An error doomed the scope's transaction; its owner rolls everything back.
        }
    }

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
