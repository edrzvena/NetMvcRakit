using Microsoft.Data.SqlClient;
using Rakit.Migrations;

namespace Rakit.Core.Tests;

[Collection(DbCollection.Name)]
public class MigrationTests
{
    private static T Scalar<T>(string sql)
    {
        using var conn = new SqlConnection(TestDb.AdminConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    [Fact]
    public void Schemas_rakit_and_app_exist()
    {
        var count = Scalar<int>("SELECT COUNT(*) FROM sys.schemas WHERE name IN (N'rakit', N'app')");
        Assert.Equal(2, count);
    }

    [Fact]
    public void Users_exist_without_login()
    {
        var count = Scalar<int>(@"SELECT COUNT(*) FROM sys.database_principals
            WHERE name IN (N'rakit_app', N'rakit_exec') AND type = 'S' AND authentication_type_desc = 'NONE'");
        Assert.Equal(2, count);
    }

    [Fact]
    public void Sample_sp_returns_rows()
    {
        using var conn = new SqlConnection(TestDb.AdminConnectionString);
        conn.Open();
        using var cmd = new SqlCommand("app.SpCustomers_List", conn) { CommandType = System.Data.CommandType.StoredProcedure };
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
    }

    [Fact]
    public void Sample_sp_count_mode()
    {
        using var conn = new SqlConnection(TestDb.AdminConnectionString);
        conn.Open();
        using var cmd = new SqlCommand("app.SpCustomers_List", conn) { CommandType = System.Data.CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("@IsCount", true);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetInt32(reader.GetOrdinal("Number")) >= 5);
    }

    [Fact]
    public void Running_migrations_twice_is_idempotent()
    {
        var result = MigrationRunner.Run(TestDb.AdminConnectionString);
        Assert.True(result.Successful);
    }
}
