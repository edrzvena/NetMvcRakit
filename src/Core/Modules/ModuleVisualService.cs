using System.Globalization;
using Microsoft.Extensions.Logging;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

namespace Rakit.Core.Modules;

/// <param name="Name">Legend text (the configured label, or the value column's name).</param>
/// <param name="Color"><c>#RRGGBB</c>, or null for the default palette.</param>
/// <param name="Data">One entry per label; null = no number for that row (a gap in the chart).</param>
public sealed record ModuleChartSeries(string Name, string? Color, IReadOnlyList<double?> Data);

/// <param name="Labels">Category text per row (dates as <c>yyyy-MM-dd[ HH:mm]</c>, an empty cell as an empty string).</param>
/// <param name="Truncated">The data source had more rows than the chart reads (<see cref="ModuleChart.MaxRows"/>, or the preview cap).</param>
public sealed record ModuleChartResult(
    Module Module,
    ChartKind Kind,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ModuleChartSeries> Series,
    bool Stacked,
    bool Horizontal,
    bool Legend,
    string? Title,
    bool Truncated);

/// <param name="Value">The raw value of the first row, or null (no row, or a NULL cell).</param>
/// <param name="Text">The value formatted on the server by the widget's format, prefix and suffix; null when there is no value.</param>
/// <param name="Label">The label column of the first row when one is configured and not empty, otherwise the fixed label, otherwise null.</param>
public sealed record ModuleWidgetResult(
    Module Module, object? Value, string? Text, string? Label, string Color, int RefreshSeconds);

public interface IModuleVisualService
{
    /// <summary>The data of a published Chart module, read through its List data source with the request's parameters.</summary>
    /// <exception cref="ModuleNotFoundException">No such module, deleted, still a draft (unless <paramref name="allowDraft"/>), or not a Chart.</exception>
    /// <exception cref="ModuleListUnavailableException">The data source cannot run, or the result lacks a configured column (generic message).</exception>
    /// <exception cref="ModuleListRequestException">A parameter value from the request does not fit.</exception>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleChartResult> GetChartAsync(Guid moduleId, bool allowDraft, ParameterContext context, CancellationToken ct = default);

    /// <summary>The data of a published Widget module (first row of its List data source).</summary>
    /// <exception cref="ModuleNotFoundException">No such module, deleted, still a draft (unless <paramref name="allowDraft"/>), or not a Widget.</exception>
    /// <exception cref="ModuleListUnavailableException"/>
    /// <exception cref="ModuleListRequestException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleWidgetResult> GetWidgetAsync(Guid moduleId, bool allowDraft, ParameterContext context, CancellationToken ct = default);

    /// <summary>For a module the caller already loaded (and checked as published / draft) with <see cref="IModuleRuntimeService.GetModuleAsync"/>.</summary>
    /// <exception cref="ModuleNotFoundException">The module is not a Chart.</exception>
    /// <exception cref="ModuleListUnavailableException"/>
    /// <exception cref="ModuleListRequestException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleChartResult> GetChartAsync(Module module, ParameterContext context, CancellationToken ct = default);

    /// <summary>For a module the caller already loaded; see <see cref="GetChartAsync(Module, ParameterContext, CancellationToken)"/>.</summary>
    /// <exception cref="ModuleNotFoundException">The module is not a Widget.</exception>
    /// <exception cref="ModuleListUnavailableException"/>
    /// <exception cref="ModuleListRequestException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleWidgetResult> GetWidgetAsync(Module module, ParameterContext context, CancellationToken ct = default);

    /// <summary>
    /// Builder preview of a Chart that is not saved: validates <paramref name="input"/>, then runs it through the data source's
    /// preview path (Query only, at most 50 rows, procedures rolled back). Unlike the runtime, a value that is not a number
    /// is an error here, so the builder sees a wrong column choice instead of an empty chart. The returned module has an empty Id.
    /// </summary>
    /// <exception cref="ModuleValidationException">The config is invalid, or a series value is not a number (messages are for the builder).</exception>
    /// <exception cref="ModuleListUnavailableException"/>
    /// <exception cref="ModuleListRequestException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleChartResult> PreviewChartAsync(ModuleInput input, ParameterContext context, CancellationToken ct = default);

    /// <summary>Builder preview of a Widget that is not saved; same rules as <see cref="PreviewChartAsync"/>.</summary>
    /// <exception cref="ModuleValidationException"/>
    /// <exception cref="ModuleListUnavailableException"/>
    /// <exception cref="ModuleListRequestException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleWidgetResult> PreviewWidgetAsync(ModuleInput input, ParameterContext context, CancellationToken ct = default);
}

public sealed class ModuleVisualService(
    IModuleRepository modules, IDataSourceService dataSources, ILogger<ModuleVisualService> logger) : IModuleVisualService
{
    public async Task<ModuleChartResult> GetChartAsync(Guid moduleId, bool allowDraft, ParameterContext context, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(moduleId, allowDraft, ModuleType.Chart, ct);
        return await RunChartAsync(module, context, preview: false, ct);
    }

    public async Task<ModuleWidgetResult> GetWidgetAsync(Guid moduleId, bool allowDraft, ParameterContext context, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(moduleId, allowDraft, ModuleType.Widget, ct);
        return await RunWidgetAsync(module, context, preview: false, ct);
    }

    public Task<ModuleChartResult> GetChartAsync(Module module, ParameterContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module.Type == ModuleType.Chart
            ? RunChartAsync(module, context, preview: false, ct)
            : throw new ModuleNotFoundException(module.Id);
    }

    public Task<ModuleWidgetResult> GetWidgetAsync(Module module, ParameterContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module.Type == ModuleType.Widget
            ? RunWidgetAsync(module, context, preview: false, ct)
            : throw new ModuleNotFoundException(module.Id);
    }

    public Task<ModuleChartResult> PreviewChartAsync(ModuleInput input, ParameterContext context, CancellationToken ct = default)
        => RunChartAsync(DraftOf(input, ModuleType.Chart), context, preview: true, ct);

    public Task<ModuleWidgetResult> PreviewWidgetAsync(ModuleInput input, ParameterContext context, CancellationToken ct = default)
        => RunWidgetAsync(DraftOf(input, ModuleType.Widget), context, preview: true, ct);

    private async Task<Module> GetModuleAsync(Guid moduleId, bool allowDraft, ModuleType type, CancellationToken ct)
    {
        var module = await modules.GetAsync(moduleId, ct);
        if (module is null || module.Type != type || (!allowDraft && module.Status != ModuleStatus.Published))
            throw new ModuleNotFoundException(moduleId);
        return module;
    }

    private static Module DraftOf(ModuleInput input, ModuleType type)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = ModuleInputValidator.Validate(input).ToList();
        if (input.Type != type) errors.Add($"Preview ini hanya untuk module bertipe {type}.");
        if (errors.Count > 0) throw new ModuleValidationException(errors);

        return new Module(
            Guid.Empty, input.Name.Trim(), input.Type, input.Description, ModuleStatus.Draft, input.GroupId, input.Config,
            DateTime.UtcNow, null);
    }

    private async Task<ModuleChartResult> RunChartAsync(Module module, ParameterContext context, bool preview, CancellationToken ct)
    {
        var chart = module.Config.Chart ?? throw Unavailable(module, "the module has no chart configuration", null);
        var maxRows = Math.Clamp(chart.MaxRows, 1, ModuleChart.HardMaxRows);

        // One row more than the chart shows, so a longer result can be told apart from one that just fits. A Table data source
        // never returns more than TableQueryRequest.MaxTake rows, so with maxRows at that size the extra row never comes: ask
        // for the total instead (data sources that cannot count leave it null; their own row cap marks the cut).
        var needTotal = maxRows >= TableQueryRequest.MaxTake;
        var (list, total) = await RunAsync(module, context, take: maxRows + 1, includeTotal: needTotal, preview, ct);

        var labelIndex = IndexOf(list.Columns, chart.LabelField.Trim());
        if (labelIndex < 0) throw Unavailable(module, $"the result has no column '{chart.LabelField}' for the labels", null);

        var seriesIndexes = new List<int>();
        foreach (var series in chart.Series)
        {
            var index = IndexOf(list.Columns, series.ValueField.Trim());
            if (index < 0) throw Unavailable(module, $"the result has no column '{series.ValueField}' for a series", null);
            seriesIndexes.Add(index);
        }

        var rows = list.Rows.Take(maxRows).ToList();
        var labels = rows.Select(row => LabelText(Cell(row, labelIndex))).ToList();

        var seriesResult = new List<ModuleChartSeries>();
        for (var s = 0; s < chart.Series.Count; s++)
        {
            var data = new List<double?>(rows.Count);
            for (var r = 0; r < rows.Count; r++)
            {
                var value = Cell(rows[r], seriesIndexes[s]);
                var number = ToNumber(value);
                if (preview && value is not null && number is null)
                    throw new ModuleValidationException([
                        $"Kolom '{chart.Series[s].ValueField}' bukan angka (baris {r + 1}: \"{ModuleCellFormatter.Plain(value)}\"). Pilih kolom numerik."]);
                data.Add(number);
            }

            var name = string.IsNullOrWhiteSpace(chart.Series[s].Label) ? chart.Series[s].ValueField.Trim() : chart.Series[s].Label!.Trim();
            var color = string.IsNullOrWhiteSpace(chart.Series[s].Color) ? null : chart.Series[s].Color!.Trim();
            seriesResult.Add(new ModuleChartSeries(name, color, data));
        }

        // Also truncated when the executor cut the result at its own cap (the preview's 50 rows, or 1000 otherwise).
        var truncated = list.Rows.Count > maxRows || list.Truncated || (total is { } count && count > maxRows);

        var title = string.IsNullOrWhiteSpace(chart.Title) ? null : chart.Title.Trim();
        return new ModuleChartResult(module, chart.Kind, labels, seriesResult, chart.Stacked, chart.Horizontal, chart.Legend, title, truncated);
    }

    private async Task<ModuleWidgetResult> RunWidgetAsync(Module module, ParameterContext context, bool preview, CancellationToken ct)
    {
        var widget = module.Config.Widget ?? throw Unavailable(module, "the module has no widget configuration", null);

        var (list, _) = await RunAsync(module, context, take: 1, includeTotal: false, preview, ct);

        var valueIndex = IndexOf(list.Columns, widget.ValueField.Trim());
        if (valueIndex < 0) throw Unavailable(module, $"the result has no column '{widget.ValueField}' for the value", null);

        var labelField = widget.LabelField?.Trim();
        var labelIndex = string.IsNullOrEmpty(labelField) ? -1 : IndexOf(list.Columns, labelField);
        if (!string.IsNullOrEmpty(labelField) && labelIndex < 0)
            throw Unavailable(module, $"the result has no column '{labelField}' for the label", null);

        var row = list.Rows.Count > 0 ? list.Rows[0] : null;
        var value = row is null ? null : Cell(row, valueIndex);

        if (preview && value is not null && widget.Format == ColumnFormat.Number && ToNumber(value) is null)
            throw new ModuleValidationException([
                $"Kolom '{widget.ValueField}' bukan angka (\"{ModuleCellFormatter.Plain(value)}\"). Pilih kolom numerik atau ubah formatnya."]);

        var label = labelIndex >= 0 && row is not null ? ModuleCellFormatter.Plain(Cell(row, labelIndex)) : null;
        if (string.IsNullOrWhiteSpace(label)) label = string.IsNullOrWhiteSpace(widget.Label) ? null : widget.Label.Trim();

        return new ModuleWidgetResult(module, value, FormatValue(value, widget), label, widget.Color, widget.RefreshSeconds);
    }

    /// <summary>Runs the module's List data source from a Skip 0 / Take <paramref name="take"/> request; returns its list result set and the total row count when asked for and known.</summary>
    private async Task<(DataTableResult List, int? Total)> RunAsync(
        Module module, ParameterContext context, int take, bool includeTotal, bool preview, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var dataSourceId = module.Config.Source.List?.DataSourceId;
        if (dataSourceId is null || dataSourceId == Guid.Empty)
            throw Unavailable(module, "the module has no list data source", null);

        DataSourceResult result;
        try
        {
            var run = new DataSourceRunRequest(0, take, IncludeTotal: includeTotal);
            result = preview
                ? await dataSources.ExecuteForPreviewAsync(dataSourceId.Value, context, run, ct)
                : await dataSources.ExecuteAsync(dataSourceId.Value, context, run, ct);
        }
        catch (Exception ex) when (ex is DataSourceNotFoundException or DataSourceForbiddenException or ParameterResolutionException
            or TableDefinitionException or SqlTemplateException or SchemaAccessDeniedException or SchemaObjectNotFoundException
            or ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw Unavailable(module, ex.GetType().Name + ": " + ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new ModuleListRequestException(ExceptionMessages.WithoutParameterSuffix(ex), ex);
        }

        var list = result.List ?? throw Unavailable(module, "the data source returned no result set at the list position", null);
        return (list, result.TotalCount);
    }

    /// <summary>The widget's value as the card shows it: formatted by its format (numbers with the configured decimals), then prefix and suffix.</summary>
    private static string? FormatValue(object? value, ModuleWidget widget)
    {
        if (value is null) return null;

        string? text;
        if (widget.Format == ColumnFormat.Number && ToDecimal(value) is { } number)
        {
            var decimals = Math.Clamp(widget.Decimals, 0, ModuleWidget.MaxDecimals);
            text = number.ToString("#,##0" + (decimals > 0 ? "." + new string('0', decimals) : ""), ModuleCellFormatter.Culture);
        }
        else
        {
            text = ModuleCellFormatter.Format(value, widget.Format);
        }

        return text is null ? null : (widget.Prefix ?? "") + text + (widget.Suffix ?? "");
    }

    private static string LabelText(object? value) => value switch
    {
        null => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? ModuleCellFormatter.Format(d, ColumnFormat.Date)! : ModuleCellFormatter.Format(d, ColumnFormat.DateTime)!,
        DateTimeOffset d => d.TimeOfDay == TimeSpan.Zero ? ModuleCellFormatter.Format(d, ColumnFormat.Date)! : ModuleCellFormatter.Format(d, ColumnFormat.DateTime)!,
        _ => ModuleCellFormatter.Plain(value) ?? "",
    };

    /// <summary>A database value as a chart number (invariant culture for text); null for NULL, non-numbers, NaN and infinity.</summary>
    private static double? ToNumber(object? value)
    {
        double number;
        switch (value)
        {
            case null: return null;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                break;
            default: return null;
        }
        return double.IsFinite(number) ? number : null;
    }

    private static decimal? ToDecimal(object? value)
    {
        try
        {
            return value switch
            {
                byte or sbyte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                float or double => double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture))
                    ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : null,
                string text when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static object? Cell(IReadOnlyList<object?> row, int index) => index >= 0 && index < row.Count ? row[index] : null;

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private ModuleListUnavailableException Unavailable(Module module, string reason, Exception? inner)
    {
        logger.LogWarning(inner, "Module {ModuleId} ({ModuleName}) cannot produce its data: {Reason}", module.Id, module.Name, reason);
        return new ModuleListUnavailableException("Data untuk halaman ini tidak tersedia saat ini.", reason, inner);
    }
}
