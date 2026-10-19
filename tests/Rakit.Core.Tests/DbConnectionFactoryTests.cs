using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Data;

namespace Rakit.Core.Tests;

[Collection(DbCollection.Name)]
public class DbConnectionFactoryTests
{
    private static DbConnectionFactory CreateFactory() => new(Options.Create(TestDb.Options));

    private static void RunAsAdmin(string sql)
    {
        using var conn = new SqlConnection(TestDb.AdminConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static string CurrentUser(SqlConnection conn)
    {
        using var cmd = new SqlCommand("SELECT USER_NAME()", conn);
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task Metadata_connection_runs_as_rakit_app()
    {
        await using var conn = await CreateFactory().CreateMetadataAsync();
        Assert.Equal("rakit_app", CurrentUser(conn));
    }

    [Fact]
    public async Task Execution_connection_runs_as_rakit_exec()
    {
        await using var conn = await CreateFactory().CreateExecutionAsync();
        Assert.Equal("rakit_exec", CurrentUser(conn));
    }

    [Fact]
    public async Task Execution_cannot_read_rakit_schema()
    {
        RunAsAdmin("IF OBJECT_ID(N'rakit.Probe', N'U') IS NULL CREATE TABLE rakit.Probe (Id int)");
        await using var conn = await CreateFactory().CreateExecutionAsync();
        using var cmd = new SqlCommand("SELECT * FROM rakit.Probe", conn);
        var ex = Assert.Throws<SqlException>(() => cmd.ExecuteScalar());
        Assert.Equal(229, ex.Number);
    }

    [Fact]
    public async Task Execution_can_execute_app_sp()
    {
        await using var conn = await CreateFactory().CreateExecutionAsync();
        using var cmd = new SqlCommand("app.SpCustomers_List", conn) { CommandType = System.Data.CommandType.StoredProcedure };
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
    }

    [Fact]
    public async Task Execution_cannot_create_objects_in_app()
    {
        await using var conn = await CreateFactory().CreateExecutionAsync();
        using var cmd = new SqlCommand("CREATE TABLE app.ShouldNotExist (Id int)", conn);
        Assert.Throws<SqlException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task Execution_cannot_revert_to_a_higher_identity()
    {
        await using var conn = await CreateFactory().CreateExecutionAsync();
        using (var revert = new SqlCommand("REVERT", conn))
            Assert.Throws<SqlException>(() => revert.ExecuteNonQuery());
        Assert.Equal("rakit_exec", CurrentUser(conn));
    }

    [Fact]
    public async Task Metadata_cannot_read_app_data()
    {
        await using var conn = await CreateFactory().CreateMetadataAsync();
        using var cmd = new SqlCommand("SELECT * FROM app.Customers", conn);
        var ex = Assert.Throws<SqlException>(() => cmd.ExecuteScalar());
        Assert.Equal(229, ex.Number);
    }

    [Fact]
    public async Task Pooling_is_forced_off_so_repeated_opens_keep_identity()
    {
        // TestDb.MetadataConnectionString has pooling enabled; the factory must neutralise that.
        var factory = CreateFactory();
        for (var i = 0; i < 5; i++)
        {
            await using var conn = await factory.CreateMetadataAsync();
            Assert.Equal("rakit_app", CurrentUser(conn));
        }
    }
}
