using System.Data;
using Microsoft.Data.SqlClient;
using Rakit.Core.Data;

namespace Rakit.Core.DataSources;

public sealed class DataSourceRepository(IDbConnectionFactory connections) : IDataSourceRepository
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    private const string SelectColumns =
        "Id, Code, Name, Mode, StatementType, Version, Definition, Created, Updated";

    public async Task<IReadOnlyList<DataSourceSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Id, Code, Name, Mode, StatementType, Version, Updated
FROM rakit.DataSources
WHERE IsDeleted = 0
ORDER BY [Order], Code";

        var result = new List<DataSourceSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DataSourceSummary(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                DataSourceJson.ModeFromColumn(reader.GetString(3)),
                DataSourceJson.StatementFromColumn(reader.GetString(4)),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }
        return result;
    }

    public async Task<DataSource?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        return await ReadOneAsync(conn, null, "Id = @key", new SqlParameter("@key", SqlDbType.UniqueIdentifier) { Value = id }, ct);
    }

    public async Task<DataSource?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        return await ReadOneAsync(conn, null, "Code = @key", new SqlParameter("@key", SqlDbType.NVarChar, 50) { Value = code ?? "" }, ct);
    }

    public async Task<DataSource> CreateAsync(DataSourceInput input, string user, CancellationToken ct = default)
    {
        EnsureValid(input);
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var definitionJson = DataSourceJson.Serialize(input.Definition);

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO rakit.DataSources (Id, Code, Name, Mode, StatementType, Version, Definition, CreatedBy, Created)
VALUES (@id, @code, @name, @mode, @type, 1, @def, @user, @now)";
                AddCommonParameters(cmd, id, input, definitionJson, user, now);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await InsertVersionAsync(conn, tx, id, 1, input, definitionJson, user, now, ct);
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            throw new DuplicateDataSourceCodeException(input.Code);
        }

        return (await GetAsync(id, ct))!;
    }

    public async Task<DataSource> UpdateAsync(Guid id, DataSourceInput input, string user, CancellationToken ct = default)
    {
        EnsureValid(input);
        var now = DateTime.UtcNow;
        var definitionJson = DataSourceJson.Serialize(input.Definition);

        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            int newVersion;
            await using (var read = conn.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT Version FROM rakit.DataSources WITH (UPDLOCK, ROWLOCK) WHERE Id = @id AND IsDeleted = 0";
                read.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                var current = await read.ExecuteScalarAsync(ct);
                if (current is null) throw new DataSourceNotFoundException(id);
                newVersion = (int)current + 1;
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"
UPDATE rakit.DataSources
SET Code = @code, Name = @name, Mode = @mode, StatementType = @type, Definition = @def,
    Version = @version, UpdatedBy = @user, Updated = @now
WHERE Id = @id";
                AddCommonParameters(cmd, id, input, definitionJson, user, now);
                cmd.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = newVersion });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await InsertVersionAsync(conn, tx, id, newVersion, input, definitionJson, user, now, ct);
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is UniqueIndexViolation or UniqueConstraintViolation)
        {
            throw new DuplicateDataSourceCodeException(input.Code);
        }

        return (await GetAsync(id, ct))!;
    }

    public async Task DeleteAsync(Guid id, string user, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rakit.DataSources SET IsDeleted = 1, UpdatedBy = @user, Updated = @now WHERE Id = @id AND IsDeleted = 0";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = DateTime.UtcNow });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new DataSourceNotFoundException(id);
    }

    public async Task<IReadOnlyList<DataSourceVersion>> GetVersionsAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT Version, Name, Mode, StatementType, Definition, ChangedBy, Changed
FROM rakit.DataSourceVersions
WHERE DataSourceId = @id
ORDER BY Version DESC";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });

        var result = new List<DataSourceVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DataSourceVersion(
                reader.GetInt32(0), reader.GetString(1),
                DataSourceJson.ModeFromColumn(reader.GetString(2)),
                DataSourceJson.StatementFromColumn(reader.GetString(3)),
                DataSourceJson.Deserialize(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetDateTime(6)));
        }
        return result;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void EnsureValid(DataSourceInput input)
    {
        var errors = DataSourceInputValidator.Validate(input);
        if (errors.Count > 0) throw new DataSourceValidationException(errors);
    }

    private static void AddCommonParameters(
        SqlCommand cmd, Guid id, DataSourceInput input, string definitionJson, string user, DateTime now)
    {
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@code", SqlDbType.NVarChar, 50) { Value = input.Code });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = input.Name });
        cmd.Parameters.Add(new SqlParameter("@mode", SqlDbType.NVarChar, 20) { Value = DataSourceJson.ModeToColumn(input.Mode) });
        cmd.Parameters.Add(new SqlParameter("@type", SqlDbType.NVarChar, 20) { Value = DataSourceJson.StatementToColumn(input.StatementType) });
        cmd.Parameters.Add(new SqlParameter("@def", SqlDbType.NVarChar, -1) { Value = definitionJson });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
    }

    private static async Task InsertVersionAsync(
        SqlConnection conn, SqlTransaction tx, Guid id, int version, DataSourceInput input,
        string definitionJson, string user, DateTime now, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT INTO rakit.DataSourceVersions (Id, DataSourceId, Version, Name, Mode, StatementType, Definition, ChangedBy, Changed)
VALUES (NEWID(), @id, @version, @name, @mode, @type, @def, @user, @now)";
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        cmd.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = version });
        cmd.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 200) { Value = input.Name });
        cmd.Parameters.Add(new SqlParameter("@mode", SqlDbType.NVarChar, 20) { Value = DataSourceJson.ModeToColumn(input.Mode) });
        cmd.Parameters.Add(new SqlParameter("@type", SqlDbType.NVarChar, 20) { Value = DataSourceJson.StatementToColumn(input.StatementType) });
        cmd.Parameters.Add(new SqlParameter("@def", SqlDbType.NVarChar, -1) { Value = definitionJson });
        cmd.Parameters.Add(new SqlParameter("@user", SqlDbType.NVarChar, -1) { Value = user ?? "" });
        cmd.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary><paramref name="where"/> is always a fixed literal from this class, never input.</summary>
    private static async Task<DataSource?> ReadOneAsync(
        SqlConnection conn, SqlTransaction? tx, string where, SqlParameter key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {SelectColumns} FROM rakit.DataSources WHERE {where} AND IsDeleted = 0";
        cmd.Parameters.Add(key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new DataSource(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            DataSourceJson.ModeFromColumn(reader.GetString(3)),
            DataSourceJson.StatementFromColumn(reader.GetString(4)),
            reader.GetInt32(5),
            DataSourceJson.Deserialize(reader.GetString(6)),
            reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8));
    }
}
