using System.Data;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.Modules;

public sealed class MenuRepository(IDbConnectionFactory connections) : IMenuRepository
{
    private const string SelectColumns =
        "Id, Label, Icon, ModuleId, Url, ParentId, IsDelimiter, IsActived, [Order], Created, Updated";

    public async Task<IReadOnlyList<Menu>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM rakit.Menus WHERE IsDeleted = 0 ORDER BY [Order], Label";

        var result = new List<Menu>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Read(reader));
        return result;
    }

    public async Task<Menu?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        return await ReadOneAsync(conn, null, id, ct);
    }

    public async Task<Menu> CreateAsync(MenuInput input, string user, CancellationToken ct = default)
    {
        var errors = new List<string>(MenuInputValidator.Validate(input));
        await using var conn = await connections.CreateMetadataAsync(ct);
        if (errors.Count == 0) await CheckReferencesAsync(conn, null, input, errors, ct);
        if (errors.Count > 0) throw new MenuValidationException(errors);

        var id = Guid.NewGuid();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO rakit.Menus (Id, Label, Icon, ModuleId, Url, ParentId, IsDelimiter, IsActived, [Order], CreatedBy, Created)
VALUES (@id, @label, @icon, @module, @url, @parent, @delimiter, @active, @order, @user, @now)";
        AddContentParameters(cmd, input);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        await cmd.ExecuteNonQueryAsync(ct);

        return (await ReadOneAsync(conn, null, id, ct))!;
    }

    public async Task<Menu> UpdateAsync(Guid id, MenuInput input, string user, CancellationToken ct = default)
    {
        var errors = new List<string>(MenuInputValidator.Validate(input));
        await using var conn = await connections.CreateMetadataAsync(ct);
        if (await ReadOneAsync(conn, null, id, ct) is null) throw new MenuNotFoundException(id);
        if (errors.Count == 0) await CheckReferencesAsync(conn, id, input, errors, ct);
        if (errors.Count > 0) throw new MenuValidationException(errors);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE rakit.Menus
SET Label = @label, Icon = @icon, ModuleId = @module, Url = @url, ParentId = @parent, IsDelimiter = @delimiter,
    IsActived = @active, [Order] = @order, UpdatedBy = @user, Updated = @now
WHERE Id = @id AND IsDeleted = 0";
        AddContentParameters(cmd, input);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new MenuNotFoundException(id);

        return (await ReadOneAsync(conn, null, id, ct))!;
    }

    public async Task DeleteAsync(Guid id, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM rakit.Menus WITH (UPDLOCK, HOLDLOCK) WHERE ParentId = @id AND IsDeleted = 0";
            count.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            var children = (int)(await count.ExecuteScalarAsync(ct))!;
            if (children > 0) throw new MenuHasChildrenException(id, children);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE rakit.Menus SET IsDeleted = 1, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new MenuNotFoundException(id);

        await tx.CommitAsync(ct);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Checks that need the database: the module exists, and the parent is a valid (root) parent.</summary>
    private static async Task CheckReferencesAsync(
        SqlConnection conn, Guid? selfId, MenuInput input, List<string> errors, CancellationToken ct)
    {
        if (input.ModuleId is { } moduleId)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM rakit.Modules WHERE Id = @id AND IsDeleted = 0";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = moduleId });
            if (await cmd.ExecuteScalarAsync(ct) is null) errors.Add("Module tidak ditemukan.");
        }

        if (input.ParentId is not { } parentId) return;

        if (parentId == selfId)
        {
            errors.Add("Menu tidak boleh menjadi induk dirinya sendiri.");
            return;
        }

        var parent = await ReadOneAsync(conn, null, parentId, ct);
        if (parent is null) errors.Add("Induk menu tidak ditemukan.");
        else if (parent.IsDelimiter) errors.Add("Delimiter tidak bisa menjadi induk.");
        else if (parent.ParentId is not null) errors.Add("Maksimal dua tingkat: induk harus menu tingkat atas.");

        if (selfId is { } id)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rakit.Menus WHERE ParentId = @id AND IsDeleted = 0";
            cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
            if ((int)(await cmd.ExecuteScalarAsync(ct))! > 0)
                errors.Add("Menu yang punya anak tidak bisa dijadikan anak menu lain.");
        }
    }

    private static void AddContentParameters(SqlCommand cmd, MenuInput input)
    {
        cmd.Parameters.Add(new SqlParameter("@label", SqlDbType.NVarChar, 100) { Value = (input.Label ?? "").Trim() });
        cmd.Parameters.Add(new SqlParameter("@icon", SqlDbType.NVarChar, 50)
        {
            Value = string.IsNullOrWhiteSpace(input.Icon) ? DBNull.Value : input.Icon.Trim(),
        });
        cmd.Parameters.Add(new SqlParameter("@module", SqlDbType.UniqueIdentifier)
        {
            Value = !input.IsDelimiter && input.ModuleId is { } m ? m : DBNull.Value,
        });
        cmd.Parameters.Add(new SqlParameter("@url", SqlDbType.NVarChar, 500)
        {
            Value = !input.IsDelimiter && !string.IsNullOrWhiteSpace(input.Url) ? input.Url.Trim() : DBNull.Value,
        });
        cmd.Parameters.Add(new SqlParameter("@parent", SqlDbType.UniqueIdentifier)
        {
            Value = input.ParentId is { } p ? p : DBNull.Value,
        });
        cmd.Parameters.Add(new SqlParameter("@delimiter", SqlDbType.Bit) { Value = input.IsDelimiter });
        cmd.Parameters.Add(new SqlParameter("@active", SqlDbType.Bit) { Value = input.IsActive });
        cmd.Parameters.Add(new SqlParameter("@order", SqlDbType.Int) { Value = input.Order });
    }

    private static Menu Read(SqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetGuid(5),
        reader.GetBoolean(6), reader.GetBoolean(7), reader.GetInt32(8),
        reader.GetDateTime(9), reader.IsDBNull(10) ? null : reader.GetDateTime(10));

    private static async Task<Menu?> ReadOneAsync(SqlConnection conn, SqlTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {SelectColumns} FROM rakit.Menus WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }
}
