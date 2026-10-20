using Rakit.Core.Modules;

namespace Rakit.Web.Areas.Object.Models;

/// <summary>
/// Turns the data of a Chart / Widget module into the JSON the page draws. Shared by the runtime data endpoint and the
/// Builder preview, so the preview shows exactly what the page will.
/// </summary>
internal static class ObjectVisualMapping
{
    public static ObjectChartData ToChart(ModuleChartResult chart) => new(
        chart.Kind.ToString().ToLowerInvariant(), chart.Labels,
        chart.Series.Select(s => new ObjectChartSeries(s.Name, s.Color, s.Data)).ToList(),
        chart.Stacked, chart.Horizontal, chart.Legend, chart.Title, chart.Truncated);

    public static ObjectWidgetData ToWidget(ModuleWidgetResult widget) => new(
        WidgetValue(widget.Value), widget.Text, widget.Label, widget.Color, widget.RefreshSeconds);

    /// <summary>The raw value for JSON: numbers and booleans as they are, anything else (dates, text, bytes) as invariant text.</summary>
    private static object? WidgetValue(object? value) => value switch
    {
        null => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
        float f when float.IsFinite(f) => f,
        double d when double.IsFinite(d) => d,
        _ => ModuleCellFormatter.Plain(value),
    };
}
