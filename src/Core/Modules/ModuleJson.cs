using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rakit.Core.Modules;

/// <summary>JSON and column-value conversions for the rakit.Modules tables.</summary>
public static class ModuleJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(ModuleConfig config) => JsonSerializer.Serialize(config, Options);

    public static ModuleConfig Deserialize(string json)
        => JsonSerializer.Deserialize<ModuleConfig>(json, Options) ?? new ModuleConfig();

    public static string TypeToColumn(ModuleType type) => type switch
    {
        ModuleType.Table => "table",
        ModuleType.Form => "form",
        ModuleType.Page => "page",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ModuleType TypeFromColumn(string value) => value switch
    {
        "table" => ModuleType.Table,
        "form" => ModuleType.Form,
        "page" => ModuleType.Page,
        _ => throw new InvalidOperationException($"Unknown module type '{value}'."),
    };

    public static string StatusToColumn(ModuleStatus status) => status == ModuleStatus.Published ? "published" : "draft";

    public static ModuleStatus StatusFromColumn(string value) => value switch
    {
        "draft" => ModuleStatus.Draft,
        "published" => ModuleStatus.Published,
        _ => throw new InvalidOperationException($"Unknown module status '{value}'."),
    };
}
