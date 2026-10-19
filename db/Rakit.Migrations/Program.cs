using Rakit.Migrations;

// Usage: dotnet run --project db/Rakit.Migrations [connectionString]
// Falls back to RAKIT_MIGRATE_CONN, then to the local dev database.
var connectionString =
    args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("RAKIT_MIGRATE_CONN")
    ?? @"Server=.\SQLEXPRESS01;Database=Rakit;Integrated Security=true;TrustServerCertificate=true";

var result = MigrationRunner.Run(connectionString, Console.WriteLine);

if (!result.Successful)
{
    Console.Error.WriteLine($"Migration failed: {result.Error?.Message}");
    return 1;
}

Console.WriteLine("Migration completed.");
return 0;
