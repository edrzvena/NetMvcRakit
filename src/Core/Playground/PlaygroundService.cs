using System.Data;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.Schema;

namespace Rakit.Core.Playground;

public sealed class PlaygroundService(
    IDbConnectionFactory connections,
    IObjectCatalog catalog,
    IOptions<RakitDbOptions> dbOptions,
    IOptions<RakitPlaygroundOptions> playgroundOptions,
    ILogger<PlaygroundService> logger) : IPlaygroundService
{
    private const int SqlTimeoutErrorNumber = -2;

    // permission-denied family: the SELECT/EXECUTE-only user cannot write
    private static readonly int[] PermissionErrors = [229, 262, 297, 300, 15151];

    private RakitPlaygroundOptions Config => playgroundOptions.Value;
    private string[] Schemas => dbOptions.Value.AllowedSchemas;

    public PlaygroundSettings Settings => new(
        Config.Enabled, Config.AllowWrite, Math.Clamp(Config.MaxRows, 1, RakitPlaygroundOptions.MaxRowsLimit),
        RakitPlaygroundOptions.MaxRowsLimit, Math.Clamp(Config.TimeoutSeconds, 1, RakitPlaygroundOptions.MaxTimeoutSeconds),
        RakitPlaygroundOptions.MaxTimeoutSeconds, Schemas.Select(s => s.Trim()).ToList());

    private void EnsureEnabled()
    {
        if (!Config.Enabled) throw new PlaygroundDisabledException();
    }

    // ---- tree ----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PlaygroundTreeSchema>> GetTreeAsync(CancellationToken ct = default)
    {
        EnsureEnabled();

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        var (inClause, parameters) = SchemaBrowserService.AllowedSchemaFilter(Schemas);
        cmd.CommandText = $@"
SELECT s.name, o.name, o.type
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0 AND s.name IN ({inClause})
ORDER BY s.name, o.name";
        cmd.Parameters.AddRange(parameters);

        var bySchema = Schemas.Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(s => s, _ => new List<PlaygroundObject>(), StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            if (bySchema.TryGetValue(schema, out var list))
                list.Add(new PlaygroundObject(schema, reader.GetString(1), ObjectCatalog.KindOf(reader.GetString(2).Trim())));
        }
        return bySchema.Select(kv => new PlaygroundTreeSchema(kv.Key, kv.Value)).ToList();
    }

    // ---- object detail -------------------------------------------------------------------------

    public Task<PlaygroundObjectDetail> GetObjectAsync(string schema, string name, CancellationToken ct = default)
    {
        EnsureEnabled();
        return catalog.GetObjectAsync(schema, name, ct);
    }

    // ---- scripts -------------------------------------------------------------------------------

    public async Task<string> ScriptAsync(string kind, string schema, string name, CancellationToken ct = default)
    {
        EnsureEnabled();
        var d = await GetObjectAsync(schema, name, ct);
        var isTable = d.Kind == "TABLE";
        var isCode = d.Kind is "VIEW" or "PROCEDURE" or "FUNCTION";

        string Require(bool allowed, Func<string> make)
            => allowed ? make() : throw new ArgumentException($"Skrip '{kind}' tidak tersedia untuk {d.Kind.ToLowerInvariant()}.");

        return kind switch
        {
            "select" => Require(d.Kind is "TABLE" or "VIEW", () => PlaygroundScripts.SelectTop(d)),
            "insert" => Require(isTable, () => PlaygroundScripts.Insert(d)),
            "update" => Require(isTable, () => PlaygroundScripts.Update(d)),
            "delete" => Require(isTable, () => PlaygroundScripts.Delete(d)),
            "exec" => Require(d.Kind == "PROCEDURE", () => PlaygroundScripts.Exec(d)),
            "create" => Require(isTable, () => PlaygroundScripts.CreateTable(d)),
            "addColumn" => Require(isTable, () => PlaygroundScripts.AddColumn(d)),
            "definition" => Require(isCode, () => d.Definition ?? "-- Definisi tidak dapat dibaca (terenkripsi atau tidak ada izin).\n"),
            "alter" => Require(isCode, () => d.Definition is null
                ? "-- Definisi tidak dapat dibaca (terenkripsi atau tidak ada izin).\n"
                : PlaygroundScripts.CreateOrAlter(d.Definition)),
            "drop" => PlaygroundScripts.Drop(d),
            _ => throw new ArgumentException($"Jenis skrip '{kind}' tidak dikenal."),
        };
    }

    public string ScriptNewTable(NewTableSpec spec)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.Schema) || !Schemas.Contains(spec.Schema.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Schema tidak termasuk yang diizinkan.");
        return PlaygroundScripts.NewTable(spec);
    }

    // ---- run -----------------------------------------------------------------------------------

    public async Task<PlaygroundRunResult> RunAsync(PlaygroundRunRequest request, CancellationToken ct = default)
    {
        EnsureEnabled();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Sql)) throw new ArgumentException("Tulis SQL dulu.");
        if (request.Sql.Length > Config.MaxScriptLength)
            throw new ArgumentException($"Skrip terlalu panjang (maks {Config.MaxScriptLength:N0} karakter).");

        var maxRows = request.MaxRows ?? Settings.DefaultMaxRows;
        if (maxRows is < 1 or > RakitPlaygroundOptions.MaxRowsLimit)
            throw new ArgumentException($"Batas baris harus antara 1 dan {RakitPlaygroundOptions.MaxRowsLimit}.");
        var timeout = request.TimeoutSeconds ?? Settings.DefaultTimeoutSeconds;
        if (timeout is < 1 or > RakitPlaygroundOptions.MaxTimeoutSeconds)
            throw new ArgumentException($"Timeout harus antara 1 dan {RakitPlaygroundOptions.MaxTimeoutSeconds} detik.");

        var batches = SqlBatchSplitter.Split(request.Sql);
        if (batches.Count == 0) throw new ArgumentException("Tidak ada perintah untuk dijalankan.");

        var write = Config.AllowWrite;
        var commit = write && !request.Rollback;

        var resultSets = new List<PlaygroundResultSet>();
        var messages = new List<PlaygroundMessage>();
        var batchNumber = 0;
        var stopwatch = Stopwatch.StartNew();

        await using var conn = await connections.CreatePlaygroundAsync(write, ct);
        conn.InfoMessage += (_, e) =>
        {
            foreach (SqlError info in e.Errors) messages.Add(new PlaygroundMessage(batchNumber, info.Message));
        };

        // The transaction is T-SQL text (not SqlTransaction) so a script that COMMITs or ROLLBACKs itself cannot wedge the connection.
        await ExecuteTextAsync(conn, "BEGIN TRANSACTION;", timeout, ct);
        PlaygroundError? error = null;
        var endedByScript = false;

        foreach (var batch in batches)
        {
            batchNumber++;
            try
            {
                await RunBatchAsync(conn, batch, batchNumber, maxRows, timeout, resultSets, messages, ct);
            }
            catch (SqlException ex)
            {
                ct.ThrowIfCancellationRequested();
                error = ToError(ex, batch, batchNumber, write, timeout);
                break;
            }

            // A script's own BEGIN TRAN / COMMIT nest inside ours and change nothing; only a COMMIT or ROLLBACK that closes
            // our transaction (count 0) takes the outcome out of the Playground's hands.
            if (!endedByScript && await TransactionCountAsync(conn, timeout, ct) == 0)
            {
                endedByScript = true;
                messages.Add(new PlaygroundMessage(batchNumber,
                    "Peringatan: skrip mengakhiri transaksinya sendiri (COMMIT atau ROLLBACK), jadi pilihan uji coba/commit tidak berlaku untuk bagian skrip ini."));
            }
        }

        // Success: commit or roll back as asked. Any error: roll everything back (and if the error broke the connection,
        // the server already rolled the open transaction back when the session ended).
        var finish = error is null && commit ? "COMMIT TRANSACTION" : "ROLLBACK TRANSACTION";
        try
        {
            await ExecuteTextAsync(conn, $"WHILE @@TRANCOUNT > 0 {finish};", timeout, ct);
        }
        catch (Exception ex) when (error is not null && ex is InvalidOperationException or SqlException)
        {
            // nothing left to roll back
        }

        stopwatch.Stop();
        return new PlaygroundRunResult(error is null, resultSets, messages, error, stopwatch.Elapsed, error is null && commit && !endedByScript, write);
    }

    private static async Task RunBatchAsync(
        SqlConnection conn, SqlBatch batch, int batchNumber, int maxRows, int timeout,
        List<PlaygroundResultSet> resultSets, List<PlaygroundMessage> messages, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = batch.Sql;
        cmd.CommandTimeout = timeout;
        cmd.StatementCompleted += (_, e) =>
            messages.Add(new PlaygroundMessage(batchNumber, string.Create(CultureInfo.InvariantCulture, $"({e.RecordCount} baris terpengaruh)")));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            if (reader.FieldCount == 0) continue;

            var columns = Enumerable.Range(0, reader.FieldCount)
                .Select(i => new PlaygroundColumnInfo(reader.GetName(i), reader.GetDataTypeName(i))).ToList();
            var rows = new List<IReadOnlyList<string?>>();
            long total = 0;

            // Rows past the cap are read and dropped, never cancelled: cancelling would also stop the statements after this one.
            while (await reader.ReadAsync(ct))
            {
                total++;
                if (rows.Count >= maxRows) continue;
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : FormatCell(reader, i);
                rows.Add(row);
            }
            resultSets.Add(new PlaygroundResultSet(batchNumber, columns, rows, total));
        }
        while (await reader.NextResultAsync(ct));
    }

    private static string? FormatCell(SqlDataReader reader, int ordinal)
    {
        try
        {
            return PlaygroundValueFormatter.Format(reader.GetValue(ordinal));
        }
        catch (Exception ex) when (ex is InvalidCastException or FileNotFoundException or FileLoadException or NotSupportedException)
        {
            // a CLR type (geography, hierarchyid) the client cannot materialize
            return "(tipe tidak didukung: " + reader.GetDataTypeName(ordinal) + ")";
        }
    }

    private static async Task ExecuteTextAsync(SqlConnection conn, string sql, int timeout, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeout;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> TransactionCountAsync(SqlConnection conn, int timeout, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@TRANCOUNT;";
        cmd.CommandTimeout = timeout;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private PlaygroundError ToError(SqlException ex, SqlBatch batch, int batchNumber, bool write, int timeout)
    {
        var first = ex.Errors.Count > 0 ? ex.Errors[0] : null;
        var inProcedure = first is not null && !string.IsNullOrEmpty(first.Procedure);
        // LineNumber counts from the first line of the batch; inside a procedure it counts from the procedure's own start.
        int? line = !inProcedure && first is { LineNumber: > 0 } ? batch.StartLine + first.LineNumber - 1 : null;

        var message = ex.Number == SqlTimeoutErrorNumber
            ? string.Create(CultureInfo.InvariantCulture, $"Query melebihi batas waktu {timeout} detik.")
            : ex.Message;
        var hint = !write && PermissionErrors.Contains(ex.Number)
            ? "Playground berjalan hanya baca. Untuk menulis atau membuat objek, set Rakit:Playground:AllowWrite = true (khusus pengembangan) lalu jalankan ulang aplikasi."
            : null;

        if (ex.Number != SqlTimeoutErrorNumber && !PermissionErrors.Contains(ex.Number))
            logger.LogDebug(ex, "Playground script failed (error {Number}).", ex.Number);

        return new PlaygroundError(message, line, ex.Number, ex.Class, batchNumber,
            inProcedure ? first!.Procedure : null, inProcedure ? first!.LineNumber : null, hint);
    }
}
