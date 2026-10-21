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
        => Repair(JsonSerializer.Deserialize<ModuleConfig>(json, Options) ?? new ModuleConfig());

    /// <summary>
    /// JSON may say null where the model has a part or a list ("form": null, "columns": null, a null field); the validators, the
    /// runtime and the views count on them being there, so a null part becomes its default and null items are dropped.
    /// </summary>
    private static ModuleConfig Repair(ModuleConfig config)
    {
        config.Source ??= new ModuleSource();
        config.Form ??= new ModuleForm();
        config.Form.Fields = config.Form.Fields?.OfType<ModuleFormField>().ToList() ?? [];
        foreach (var field in config.Form.Fields) field.Options = field.Options?.OfType<ModuleOption>().ToList();
        config.Display ??= new ModuleDisplay();
        config.Display.Buttons ??= new ModuleButtons();
        config.Display.Menu ??= new ModuleMenu();
        config.Display.Columns = config.Display.Columns?.OfType<ModuleColumn>().ToList() ?? [];
        config.Display.Filters = config.Display.Filters?.OfType<ModuleFilter>().ToList() ?? [];
        foreach (var filter in config.Display.Filters) filter.Options = filter.Options?.OfType<ModuleOption>().ToList();
        config.Actions = config.Actions?.OfType<ModuleAction>().ToList() ?? [];
        config.Access ??= new ModuleAccess();
        if (config.Chart is { } chart) chart.Series = chart.Series?.OfType<ChartSeries>().ToList() ?? [];
        return config;
    }

    public static string TypeToColumn(ModuleType type) => type switch
    {
        ModuleType.Table => "table",
        ModuleType.Form => "form",
        ModuleType.Page => "page",
        ModuleType.Chart => "chart",
        ModuleType.Widget => "widget",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ModuleType TypeFromColumn(string value) => value switch
    {
        "table" => ModuleType.Table,
        "form" => ModuleType.Form,
        "page" => ModuleType.Page,
        "chart" => ModuleType.Chart,
        "widget" => ModuleType.Widget,
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
