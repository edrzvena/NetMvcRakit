using System.Data;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.Modules;

public sealed class ModuleRepository(IDbConnectionFactory connections) : IModuleRepository
{
    private const string SelectColumns = "Id, Name, Type, Description, Status, GroupId, Config, Created, Updated";

    public async Task<IReadOnlyList<ModuleSummary>> ListAsync(ModuleStatus? status = null, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT m.Id, m.Name, m.Type, m.Status, m.GroupId, g.Name, m.Updated
FROM rakit.Modules m
LEFT JOIN rakit.ModuleGroups g ON g.Id = m.GroupId AND g.IsDeleted = 0
WHERE m.IsDeleted = 0 AND (@status IS NULL OR m.Status = @status)
ORDER BY g.Name, m.[Order], m.Name";
        cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 20)
        {
            Value = status is { } s ? ModuleJson.StatusToColumn(s) : DBNull.Value,
        });

        var result = new List<ModuleSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ModuleSummary(
                reader.GetGuid(0), reader.GetString(1), ModuleJson.TypeFromColumn(reader.GetString(2)),
                ModuleJson.StatusFromColumn(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }
        return result;
    }

    public async Task<Module?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        return await ReadOneAsync(conn, id, ct);
    }

    public async Task<Module> CreateAsync(ModuleInput input, string user, CancellationToken ct = default)
    {
        EnsureValid(input);
        var id = Guid.NewGuid();

        await using var conn = await connections.CreateMetadataAsync(ct);
        await EnsureGroupExistsAsync(conn, input.GroupId, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO rakit.Modules (Id, Name, Type, Description, Status, GroupId, Config, CreatedBy, Created)
VALUES (@id, @name, @type, @description, N'draft', @group, @config, @user, @now)";
        AddContentParameters(cmd, input);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        await cmd.ExecuteNonQueryAsync(ct);

        return (await ReadOneAsync(conn, id, ct))!;
    }

    public async Task<Module> UpdateAsync(Guid id, ModuleInput input, string user, CancellationToken ct = default)
    {
        EnsureValid(input);

        await using var conn = await connections.CreateMetadataAsync(ct);
        await EnsureGroupExistsAsync(conn, input.GroupId, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE rakit.Modules
SET Name = @name, Type = @type, Description = @description, GroupId = @group, Config = @config,
    UpdatedBy = @user, Updated = @now
WHERE Id = @id AND IsDeleted = 0";
        AddContentParameters(cmd, input);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new ModuleNotFoundException(id);

        return (await ReadOneAsync(conn, id, ct))!;
    }

    public async Task<Module> SetStatusAsync(Guid id, ModuleStatus status, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        var current = await ReadOneAsync(conn, id, ct) ?? throw new ModuleNotFoundException(id);

        if (status == ModuleStatus.Published)
        {
            var errors = ModuleInputValidator.Validate(ToInput(current));
            if (errors.Count > 0) throw new ModuleValidationException(errors);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rakit.Modules SET Status = @status, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 20) { Value = ModuleJson.StatusToColumn(status) });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new ModuleNotFoundException(id);

        return (await ReadOneAsync(conn, id, ct))!;
    }

    public async Task<Module> CopyAsync(Guid id, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        var source = await ReadOneAsync(conn, id, ct) ?? throw new ModuleNotFoundException(id);

        const string suffix = " (salinan)";
        var name = source.Name.Length + suffix.Length > ModuleInputValidator.MaxNameLength
            ? source.Name[..(ModuleInputValidator.MaxNameLength - suffix.Length)] + suffix
            : source.Name + suffix;
        var copy = ToInput(source);
        copy.Name = name;

        var newId = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO rakit.Modules (Id, Name, Type, Description, Status, GroupId, Config, CreatedBy, Created)
VALUES (@id, @name, @type, @description, N'draft', @group, @config, @user, @now)";
        AddContentParameters(cmd, copy);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = newId });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        await cmd.ExecuteNonQueryAsync(ct);

        return (await ReadOneAsync(conn, newId, ct))!;
    }

    public async Task DeleteAsync(Guid id, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM rakit.Menus WITH (UPDLOCK, HOLDLOCK) WHERE ModuleId = @id AND IsDeleted = 0";
            count.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            var menus = (int)(await count.ExecuteScalarAsync(ct))!;
            if (menus > 0) throw new ModuleInUseException(id, menus);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE rakit.Modules SET IsDeleted = 1, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new ModuleNotFoundException(id);

        await tx.CommitAsync(ct);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void EnsureValid(ModuleInput input)
    {
        var errors = ModuleInputValidator.Validate(input);
        if (errors.Count > 0) throw new ModuleValidationException(errors);
    }

    private static ModuleInput ToInput(Module m) => new()
    {
        Name = m.Name, Type = m.Type, Description = m.Description, GroupId = m.GroupId,
        // round-trip through JSON so the copy never shares objects with the source
        Config = ModuleJson.Deserialize(ModuleJson.Serialize(m.Config)),
    };

    private static void AddContentParameters(SqlCommand cmd, ModuleInput input)
    {
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = input.Name.Trim() });
        cmd.Parameters.Add(new SqlParameter("@type", SqlDbType.NVarChar, 20) { Value = ModuleJson.TypeToColumn(input.Type) });
        cmd.Parameters.Add(new SqlParameter("@description", SqlDbType.NVarChar, 500)
        {
            Value = string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description.Trim(),
        });
        cmd.Parameters.Add(new SqlParameter("@group", SqlDbType.UniqueIdentifier)
        {
            Value = input.GroupId is { } g ? g : DBNull.Value,
        });
        cmd.Parameters.Add(new SqlParameter("@config", SqlDbType.NVarChar, -1) { Value = ModuleJson.Serialize(input.Config) });
    }

    private static async Task EnsureGroupExistsAsync(SqlConnection conn, Guid? groupId, CancellationToken ct)
    {
        if (groupId is null) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM rakit.ModuleGroups WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = groupId.Value });
        if (await cmd.ExecuteScalarAsync(ct) is null)
            throw new ModuleValidationException(["Grup module tidak ditemukan."]);
    }

    private static async Task<Module?> ReadOneAsync(SqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM rakit.Modules WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Module(
            reader.GetGuid(0), reader.GetString(1), ModuleJson.TypeFromColumn(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ModuleJson.StatusFromColumn(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            ModuleJson.Deserialize(reader.GetString(6)),
            reader.GetDateTime(7), reader.IsDBNull(8) ? null : reader.GetDateTime(8));
    }
}
