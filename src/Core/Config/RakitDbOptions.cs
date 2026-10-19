namespace Rakit.Core.Config;

public sealed class RakitDbOptions
{
    public const string SectionName = "Rakit:Db";

    public string MetadataConnectionString { get; set; } = "";
    public string ExecutionConnectionString { get; set; } = "";
    public string[] AllowedSchemas { get; set; } = ["app"];
    public bool AllowRawQuery { get; set; }
    public bool AllowWrite { get; set; }
    public bool AllowStoredProcedure { get; set; } = true;
}
