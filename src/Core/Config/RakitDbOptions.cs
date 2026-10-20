namespace Rakit.Core.Config;

public sealed class RakitDbOptions
{
    public const string SectionName = "Rakit:Db";

    public string MetadataConnectionString { get; set; } = "";
    public string ExecutionConnectionString { get; set; } = "";
    /// <summary>
    /// Explicit allow-list, deliberately without a default: configuration binding appends array items to an existing
    /// default instead of replacing it, so a default here could never be removed by configuration.
    /// Set in appsettings (<c>Rakit:Db:AllowedSchemas</c>); the validator refuses an empty list.
    /// </summary>
    public string[] AllowedSchemas { get; set; } = [];
    public bool AllowRawQuery { get; set; }
    public bool AllowWrite { get; set; }
    public bool AllowStoredProcedure { get; set; } = true;
}
