using System.Data;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.Modules;

public sealed class ModuleGroupRepository(IDbConnectionFactory connections) : IModuleGroupRepository
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int MaxNameLength = 100;

    private const string SelectColumns = "Id, Name, IsActived, Created, Updated";

    public async Task<IReadOnlyList<ModuleGroup>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM rakit.ModuleGroups WHERE IsDeleted = 0 ORDER BY [Order], Name";

        var result = new List<ModuleGroup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    public async Task<ModuleGroup?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        return await ReadOneAsync(conn, id, ct);
    }

    public async Task<ModuleGroup> CreateAsync(string name, string user, CancellationToken ct = default)
    {
        name = ValidName(name);
        var id = Guid.NewGuid();

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO rakit.ModuleGroups (Id, Name, IsActived, CreatedBy, Created) VALUES (@id, @name, 1, @user, @now)";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, MaxNameLength) { Value = name });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            throw new DuplicateModuleGroupNameException(name);
        }

        return (await ReadOneAsync(conn, id, ct))!;
    }

    public async Task<ModuleGroup> UpdateAsync(Guid id, string name, bool isActive, string user, CancellationToken ct = default)
    {
        name = ValidName(name);

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rakit.ModuleGroups SET Name = @name, IsActived = @active, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, MaxNameLength) { Value = name });
        cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = isActive });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        try
        {
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new ModuleGroupNotFoundException(id);
        }
        catch (SqlException ex) when (ex.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            throw new DuplicateModuleGroupNameException(name);
        }

        return (await ReadOneAsync(conn, id, ct))!;
    }

    public async Task DeleteAsync(Guid id, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM rakit.Modules WITH (UPDLOCK, HOLDLOCK) WHERE GroupId = @id AND IsDeleted = 0";
            count.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            var modules = (int)(await count.ExecuteScalarAsync(ct))!;
            if (modules > 0) throw new ModuleGroupInUseException(id, modules);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE rakit.ModuleGroups SET IsDeleted = 1, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new ModuleGroupNotFoundException(id);

        await tx.CommitAsync(ct);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string ValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ModuleGroupValidationException(["Nama grup wajib diisi."]);
        name = name.Trim();
        if (name.Length > MaxNameLength)
            throw new ModuleGroupValidationException([$"Nama grup maksimal {MaxNameLength} karakter."]);
        return name;
    }

    private static ModuleGroup Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2),
        reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4));

    private static async Task<ModuleGroup?> ReadOneAsync(SqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM rakit.ModuleGroups WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }
}
