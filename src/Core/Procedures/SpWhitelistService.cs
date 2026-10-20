using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.Schema;

namespace Rakit.Core.Procedures;

public sealed class SpWhitelistService(
    IDbConnectionFactory connections, ISchemaBrowserService schemaBrowser, IOptions<RakitDbOptions> options)
    : ISpWhitelistService
{
    private const int MaxIdentifierLength = 128;

    public async Task<IReadOnlyList<SpWhitelistEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Id, SchemaName, ProcName, IsActived, Created, Updated
FROM rakit.SpWhitelist
ORDER BY SchemaName, ProcName";

        var result = new List<SpWhitelistEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    public async Task<bool> IsAllowedAsync(string schema, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name)) return false;
        schema = schema.Trim();
        name = name.Trim();
        if (schema.Length > MaxIdentifierLength || name.Length > MaxIdentifierLength) return false;
        if (!options.Value.AllowedSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase)) return false;

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT 1
FROM rakit.SpWhitelist
WHERE SchemaName = @schema COLLATE Latin1_General_CI_AS
  AND ProcName = @name COLLATE Latin1_General_CI_AS
  AND IsActived = 1";
        cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, MaxIdentifierLength) { Value = schema });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, MaxIdentifierLength) { Value = name });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<SpWhitelistEntry> SetActiveAsync(
        string schema, string name, bool isActive, string user, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        schema = schema.Trim();
        name = name.Trim();

        if (!options.Value.AllowedSchemas.Contains(schema, StringComparer.OrdinalIgnoreCase))
            throw new SchemaAccessDeniedException($"Schema '{schema}' is not allowed.");

        // The catalog (already limited to AllowedSchemas, without sp_*/xp_*) decides what exists and how it is spelled.
        var procedures = await schemaBrowser.GetProceduresAsync(ct);
        var procedure = procedures.FirstOrDefault(p =>
            string.Equals(p.Schema, schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        // Only an existing procedure can be activated or newly listed. An already listed row may still be deactivated
        // after its procedure is gone; otherwise it would come back to life when a procedure of that name is recreated.
        if (procedure is null && isActive)
            throw new SchemaObjectNotFoundException("Object not found.");

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE rakit.SpWhitelist WITH (UPDLOCK, HOLDLOCK)
SET IsActived = @active, UpdatedBy = @user, Updated = @now
WHERE SchemaName = @schema COLLATE Latin1_General_CI_AS AND ProcName = @name COLLATE Latin1_General_CI_AS;

IF @@ROWCOUNT = 0 AND @exists = 1
    INSERT INTO rakit.SpWhitelist (Id, SchemaName, ProcName, IsActived, CreatedBy, Created)
    VALUES (NEWID(), @schema, @name, @active, @user, @now);

SELECT Id, SchemaName, ProcName, IsActived, Created, Updated
FROM rakit.SpWhitelist
WHERE SchemaName = @schema COLLATE Latin1_General_CI_AS AND ProcName = @name COLLATE Latin1_General_CI_AS;";
        cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, MaxIdentifierLength) { Value = procedure?.Schema ?? schema });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, MaxIdentifierLength) { Value = procedure?.Name ?? name });
        cmd.Parameters.Add(new SqlParameter("@exists", SqlDbType.Bit) { Value = procedure is not null });
        cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = isActive });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new SchemaObjectNotFoundException("Object not found.");
        return Read(reader);
    }

    private static SpWhitelistEntry Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
        reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5));
}
