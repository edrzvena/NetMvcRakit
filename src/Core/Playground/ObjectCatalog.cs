using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.Schema;

namespace Rakit.Core.Playground;

/// <summary>
/// Reads what an object in an allowed schema looks like (columns, keys, indexes, foreign keys, checks, definition, procedure
/// parameters). Unlike <see cref="IPlaygroundService"/> it does not depend on the Playground switch: the module builder
/// reads table structure through it too. Objects outside Rakit:Db:AllowedSchemas are never described.
/// </summary>
public interface IObjectCatalog
{
    /// <summary>Throws SchemaAccessDeniedException / SchemaObjectNotFoundException for a schema or object that is not available.</summary>
    Task<PlaygroundObjectDetail> GetObjectAsync(string schema, string name, CancellationToken ct = default);
}

public sealed class ObjectCatalog(
    IDbConnectionFactory connections, ISchemaBrowserService schemaBrowser, IOptions<RakitDbOptions> dbOptions) : IObjectCatalog
{
    private const int MaxIdentifierLength = 128;

    private string[] Schemas => dbOptions.Value.AllowedSchemas;

    /// <summary>TABLE, VIEW, PROCEDURE or FUNCTION for a sys.objects type code.</summary>
    internal static string KindOf(string type) => type switch
    {
        "U" => "TABLE",
        "V" => "VIEW",
        "P" => "PROCEDURE",
        _ => "FUNCTION",
    };

    public async Task<PlaygroundObjectDetail> GetObjectAsync(string schema, string name, CancellationToken ct = default)
    {
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

}
