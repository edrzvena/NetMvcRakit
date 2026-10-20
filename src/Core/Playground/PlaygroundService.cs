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
    ISchemaBrowserService schemaBrowser,
    IOptions<RakitDbOptions> dbOptions,
    IOptions<RakitPlaygroundOptions> playgroundOptions,
    ILogger<PlaygroundService> logger) : IPlaygroundService
{
    private const int MaxIdentifierLength = 128;
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
                list.Add(new PlaygroundObject(schema, reader.GetString(1), KindOf(reader.GetString(2).Trim())));
        }
        return bySchema.Select(kv => new PlaygroundTreeSchema(kv.Key, kv.Value)).ToList();
    }

    private static string KindOf(string type) => type switch
    {
        "U" => "TABLE",
        "V" => "VIEW",
        "P" => "PROCEDURE",
        _ => "FUNCTION",
    };

    // ---- object detail -------------------------------------------------------------------------

    public async Task<PlaygroundObjectDetail> GetObjectAsync(string schema, string name, CancellationToken ct = default)
    {
        EnsureEnabled();
        await using var conn = await connections.CreateMetadataAsync(ct);
        var (objectId, resolvedSchema, resolvedName, kind) = await ResolveAsync(conn, schema, name, ct);

        var columns = new List<PlaygroundColumn>();
        var indexes = new List<PlaygroundIndex>();
        var foreignKeys = new List<PlaygroundForeignKey>();
        var checks = new List<PlaygroundCheck>();
        long? approxRows = null;
        string? definition = null;
        IReadOnlyList<ProcedureParameter> parameters = [];

        if (kind is "TABLE" or "VIEW" or "FUNCTION")
            columns = await ReadColumnsAsync(conn, objectId, ct);

        if (kind == "TABLE")
        {
            indexes = await ReadIndexesAsync(conn, objectId, ct);
            var primaryKey = indexes.FirstOrDefault(i => i.IsPrimaryKey)?.KeyColumns.Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (primaryKey is not null)
                columns = columns.Select(c => primaryKey.Contains(c.Name) ? c with { IsPrimaryKey = true } : c).ToList();
            foreignKeys = await ReadForeignKeysAsync(conn, objectId, ct);
            checks = await ReadChecksAsync(conn, objectId, ct);
            approxRows = await ReadScalarAsync<long?>(conn, "SELECT SUM(CONVERT(bigint, [rows])) FROM sys.partitions WHERE object_id = @id AND index_id IN (0, 1)", objectId, ct);
        }
        else
        {
            definition = await ReadScalarAsync<string?>(conn, "SELECT OBJECT_DEFINITION(@id)", objectId, ct);
            if (kind == "PROCEDURE")
                parameters = await schemaBrowser.GetProcedureParametersAsync(resolvedSchema, resolvedName, ct);
        }

        return new PlaygroundObjectDetail(resolvedSchema, resolvedName, kind, approxRows, columns, indexes, foreignKeys, checks, parameters, definition);
    }

    private sealed record Resolved(int ObjectId, string Schema, string Name, string Kind);

    /// <summary>The caller's schema and name are only ever bound parameters; the object id and the real names come from the catalog.</summary>
    private async Task<Resolved> ResolveAsync(SqlConnection conn, string schema, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        schema = schema.Trim();
        name = name.Trim();

        if (!Schemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
            throw new SchemaAccessDeniedException($"Schema '{schema}' is not allowed.");
        if (schema.Length > MaxIdentifierLength || name.Length > MaxIdentifierLength)
            throw new SchemaObjectNotFoundException("Object not found.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT o.object_id, s.name, o.name, o.type
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema COLLATE Latin1_General_CI_AS
  AND o.name = @name COLLATE Latin1_General_CI_AS
  AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
  AND o.is_ms_shipped = 0";
        cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, MaxIdentifierLength) { Value = schema });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, MaxIdentifierLength) { Value = name });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new SchemaObjectNotFoundException("Object not found.");
        return new Resolved(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), KindOf(reader.GetString(3).Trim()));
    }

    private static async Task<T?> ReadScalarAsync<T>(SqlConnection conn, string sql, int objectId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task<List<PlaygroundColumn>> ReadColumnsAsync(SqlConnection conn, int objectId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT c.column_id, c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
       cc.definition, c.collation_name, dc.definition, ic.seed_value, ic.increment_value
FROM sys.columns c
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE c.object_id = @id
ORDER BY c.column_id";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });

        var result = new List<PlaygroundColumn>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var isIdentity = reader.GetBoolean(7);
            result.Add(new PlaygroundColumn(
                reader.GetInt32(0), reader.GetString(1),
                PlaygroundScripts.FormatType(reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5)),
                reader.GetBoolean(6), isIdentity,
                isIdentity && !reader.IsDBNull(11) ? $"({Convert.ToString(reader.GetValue(11), CultureInfo.InvariantCulture)},{Convert.ToString(reader.GetValue(12), CultureInfo.InvariantCulture)})" : null,
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                IsPrimaryKey: false));
        }
        return result;
    }

    private static async Task<List<PlaygroundIndex>> ReadIndexesAsync(SqlConnection conn, int objectId, CancellationToken ct)
    {
        var headers = new List<(int Id, string Name, string Type, bool Unique, bool Primary, bool UniqueConstraint, string? Filter)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.filter_definition
FROM sys.indexes i
WHERE i.object_id = @id AND i.index_id > 0 AND i.is_hypothetical = 0
ORDER BY i.index_id";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                headers.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        var keys = new Dictionary<int, List<PlaygroundIndexColumn>>();
        var included = new Dictionary<int, List<string>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT ic.index_id, c.name, ic.is_descending_key, ic.is_included_column
FROM sys.index_columns ic
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE ic.object_id = @id
ORDER BY ic.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                if (reader.GetBoolean(3))
                {
                    if (!included.TryGetValue(id, out var list)) included[id] = list = [];
                    list.Add(reader.GetString(1));
                }
                else
                {
                    if (!keys.TryGetValue(id, out var list)) keys[id] = list = [];
                    list.Add(new PlaygroundIndexColumn(reader.GetString(1), reader.GetBoolean(2)));
                }
            }
        }

        return headers.Select(h => new PlaygroundIndex(
            h.Name, h.Type, h.Unique, h.Primary, h.UniqueConstraint,
            keys.GetValueOrDefault(h.Id) ?? [], included.GetValueOrDefault(h.Id) ?? [], h.Filter)).ToList();
    }

    private static async Task<List<PlaygroundForeignKey>> ReadForeignKeysAsync(SqlConnection conn, int objectId, CancellationToken ct)
    {
        var headers = new List<(int Id, string Name, string Schema, string Table, string OnDelete, string OnUpdate)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT fk.object_id, fk.name, ISNULL(OBJECT_SCHEMA_NAME(fk.referenced_object_id), N''), ISNULL(OBJECT_NAME(fk.referenced_object_id), N''),
       fk.delete_referential_action_desc, fk.update_referential_action_desc
FROM sys.foreign_keys fk
WHERE fk.parent_object_id = @id
ORDER BY fk.name";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                headers.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        var parents = new Dictionary<int, List<string>>();
        var referenced = new Dictionary<int, List<string>>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT fkc.constraint_object_id, pc.name, rc.name
FROM sys.foreign_key_columns fkc
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE fkc.parent_object_id = @id
ORDER BY fkc.constraint_object_id, fkc.constraint_column_id";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                if (!parents.TryGetValue(id, out var p)) parents[id] = p = [];
                if (!referenced.TryGetValue(id, out var r)) referenced[id] = r = [];
                p.Add(reader.GetString(1));
                r.Add(reader.GetString(2));
            }
        }

        return headers.Select(h => new PlaygroundForeignKey(
            h.Name, parents.GetValueOrDefault(h.Id) ?? [], h.Schema, h.Table, referenced.GetValueOrDefault(h.Id) ?? [], h.OnDelete, h.OnUpdate)).ToList();
    }

    private static async Task<List<PlaygroundCheck>> ReadChecksAsync(SqlConnection conn, int objectId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, definition FROM sys.check_constraints WHERE parent_object_id = @id ORDER BY name";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });
        var result = new List<PlaygroundCheck>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new PlaygroundCheck(reader.GetString(0), reader.GetString(1)));
        return result;
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
