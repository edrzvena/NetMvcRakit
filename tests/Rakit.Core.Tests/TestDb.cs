using Rakit.Core.Config;

namespace Rakit.Core.Tests;

public static class TestDb
{
    public const string DatabaseName = "Rakit_Test";

    public static string BaseConnectionString { get; } =
        Environment.GetEnvironmentVariable("RAKIT_TEST_CONN")
        ?? @"Server=.\SQLEXPRESS01;Integrated Security=true;TrustServerCertificate=true";

    public static string MasterConnectionString => $"{BaseConnectionString};Database=master";

    public static string AdminConnectionString => $"{BaseConnectionString};Database={DatabaseName}";

    public static string MetadataConnectionString => AdminConnectionString;

    public static string ExecutionConnectionString => $"{AdminConnectionString};Pooling=false";

    public static RakitDbOptions Options => new()
    {
        MetadataConnectionString = MetadataConnectionString,
        ExecutionConnectionString = ExecutionConnectionString,
        AllowedSchemas = ["app"],
    };
}
