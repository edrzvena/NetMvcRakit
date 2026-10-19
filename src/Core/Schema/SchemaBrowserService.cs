using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;

namespace Rakit.Core.Schema;

public sealed class SchemaBrowserService(IDbConnectionFactory connections, IOptions<RakitDbOptions> options)
    : ISchemaBrowserService
{
    private const int MaxIdentifierLength = 128;
    private const string ResultSetWarning = "Result set tidak dapat dideteksi.";

    private RakitDbOptions Config => options.Value;

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        var (inClause, parameters) = AllowedSchemaFilter(Config.AllowedSchemas);
        cmd.CommandText = $@"
SELECT s.name, o.name, o.type
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND s.name IN ({inClause})
ORDER BY s.name, o.name";
        cmd.Parameters.AddRange(parameters);

        var result = new List<TableInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new TableInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2).Trim() == "V" ? "VIEW" : "TABLE"));
        return result;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string schema, string name, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        var target = await ResolveAsync(conn, schema, name, isProcedure: false, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, c.is_nullable
FROM sys.columns c
WHERE c.object_id = @id
ORDER BY c.column_id";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = target.ObjectId });

        var result = new List<ColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ColumnInfo(reader.GetString(0), reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4), reader.GetBoolean(5)));
        return result;
    }

    public async Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        var (inClause, parameters) = AllowedSchemaFilter(Config.AllowedSchemas);
        cmd.CommandText = $@"
SELECT s.name, o.name
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type = 'P' AND o.is_ms_shipped = 0
  AND o.name NOT LIKE N'sp[_]%' AND o.name NOT LIKE N'xp[_]%'
  AND s.name IN ({inClause})
ORDER BY s.name, o.name";
        cmd.Parameters.AddRange(parameters);

        var result = new List<ProcedureInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new ProcedureInfo(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task<ProcedureDetail> GetProcedureDetailAsync(string schema, string name, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        var target = await ResolveAsync(conn, schema, name, isProcedure: true, ct);

        var definition = await ReadDefinitionAsync(conn, target.ObjectId, ct);
        var parameters = await ReadParametersAsync(conn, target.ObjectId, definition, ct);
        var (columns, warning) = await ReadFirstResultSetAsync(target.ObjectId, ct);

        return new ProcedureDetail(target.Schema, target.Name, parameters, columns, warning);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record ResolvedObject(int ObjectId, string Schema, string Name);

    /// <summary>
    /// Validates the caller's schema/name and finds the object in the catalog. Input is only ever used as a
    /// bound parameter; the returned object_id comes from the catalog.
    /// </summary>
    private async Task<ResolvedObject> ResolveAsync(SqlConnection conn, string schema, string name, bool isProcedure, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        schema = schema.Trim();
        name = name.Trim();

        if (!Config.AllowedSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
            throw new SchemaAccessDeniedException($"Schema '{schema}' is not allowed.");

        // Longer than an identifier can be: would be silently truncated by the parameter size below.
        if (schema.Length > MaxIdentifierLength || name.Length > MaxIdentifierLength)
            throw new SchemaObjectNotFoundException("Object not found.");

        if (isProcedure && (name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)))
            throw new SchemaObjectNotFoundException("Object not found.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT o.object_id, s.name, o.name
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema COLLATE Latin1_General_CI_AS
  AND o.name = @name COLLATE Latin1_General_CI_AS
  AND o.type IN (@t1, @t2)
  AND o.is_ms_shipped = 0";
        cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, MaxIdentifierLength) { Value = schema });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, MaxIdentifierLength) { Value = name });
        cmd.Parameters.Add(new SqlParameter("@t1", SqlDbType.Char, 2) { Value = isProcedure ? "P" : "U" });
        cmd.Parameters.Add(new SqlParameter("@t2", SqlDbType.Char, 2) { Value = isProcedure ? "P" : "V" });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new SchemaObjectNotFoundException("Object not found.");
        return new ResolvedObject(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<string?> ReadDefinitionAsync(SqlConnection conn, int objectId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT OBJECT_DEFINITION(@id)";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<IReadOnlyList<ProcedureParameter>> ReadParametersAsync(
        SqlConnection conn, int objectId, string? definition, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT p.name, TYPE_NAME(p.user_type_id), p.max_length, p.precision, p.scale, p.is_output, p.has_default_value
FROM sys.parameters p
WHERE p.object_id = @id AND p.parameter_id > 0
ORDER BY p.parameter_id";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });

        var result = new List<ProcedureParameter>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var paramName = reader.GetString(0);
            // sys.parameters.has_default_value is always 0 for Transact-SQL procedures,
            // so defaults are detected from the procedure text as well.
            var hasDefault = reader.GetBoolean(6) || DeclaresDefault(definition, paramName);
            result.Add(new ProcedureParameter(
                paramName, reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4),
                reader.GetBoolean(5), hasDefault));
        }
        return result;
    }

    private static bool DeclaresDefault(string? definition, string parameterName)
    {
        if (string.IsNullOrEmpty(definition)) return false;
        // @name [AS] type [(len[,scale])] =
        var pattern = $@"(?<![\w@]){Regex.Escape(parameterName)}\s+(?:AS\s+)?\[?[\w\.]+\]?(?:\s*\(\s*[\w\s,]+\s*\))?\s*=";
        return Regex.IsMatch(definition, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// The describe DMF requires EXECUTE permission on the procedure, which rakit_app deliberately lacks,
    /// so this one read-only metadata query runs on the Execution connection (rakit_exec).
    /// </summary>
    private async Task<(IReadOnlyList<ResultColumn> Columns, string? Warning)> ReadFirstResultSetAsync(
        int objectId, CancellationToken ct)
    {
        var columns = new List<ResultColumn>();
        try
        {
            await using var conn = await connections.CreateExecutionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT name, system_type_name, is_nullable, error_message
FROM sys.dm_exec_describe_first_result_set_for_object(@id, 0)
ORDER BY column_ordinal";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.Int) { Value = objectId });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(3)) return ([], ResultSetWarning);
                if (reader.IsDBNull(0)) continue;
                columns.Add(new ResultColumn(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), !reader.IsDBNull(2) && reader.GetBoolean(2)));
            }
            return (columns, null);
        }
        catch (SqlException)
        {
            return ([], ResultSetWarning);
        }
    }

    /// <summary>Expands AllowedSchemas into "@s0, @s1, ..." plus the matching bound parameters.</summary>
    internal static (string InClause, SqlParameter[] Parameters) AllowedSchemaFilter(IReadOnlyList<string> schemas)
    {
        var parameters = schemas
            .Select((schema, i) => new SqlParameter($"@s{i}", SqlDbType.NVarChar, MaxIdentifierLength) { Value = schema.Trim() })
            .ToArray();
        return (string.Join(", ", parameters.Select(p => p.ParameterName)), parameters);
    }
}
