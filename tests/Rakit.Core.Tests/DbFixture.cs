using Microsoft.Data.SqlClient;
using Rakit.Migrations;

namespace Rakit.Core.Tests;

/// <summary>Creates a fresh Rakit_Test database via the real migrations, drops it afterwards.</summary>
public sealed class DbFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        DropDatabase();
        var result = MigrationRunner.Run(TestDb.AdminConnectionString);
        if (!result.Successful)
            throw new InvalidOperationException("Test database migration failed.", result.Error);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        DropDatabase();
        return Task.CompletedTask;
    }

    private static void DropDatabase()
    {
        SqlConnection.ClearAllPools();
        using var conn = new SqlConnection(TestDb.MasterConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
IF DB_ID(N'{TestDb.DatabaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{TestDb.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{TestDb.DatabaseName}];
END";
        cmd.ExecuteNonQuery();
    }
}

[CollectionDefinition(Name)]
public sealed class DbCollection : ICollectionFixture<DbFixture>
{
    public const string Name = "Db";
}
