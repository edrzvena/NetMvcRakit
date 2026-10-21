using Microsoft.Extensions.Logging;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

namespace Rakit.Core.Modules;

/// <param name="Skip">Rows to skip. Null = 0.</param>
/// <param name="Take">Page size. Null = the field type's default; always clamped to the field type's range.</param>
/// <param name="Search">Free text, trimmed and cut to 255 characters.</param>
public sealed record ModuleLookupRequest(int? Skip = null, int? Take = null, string? Search = null);

/// <param name="Value">What is stored (the lookup's value column as text).</param>
/// <param name="Text">What the user reads (the text column; the value when that is empty).</param>
public sealed record ModuleLookupItem(string Value, string Text);

/// <param name="Total">Null when the data source cannot count.</param>
/// <param name="HasNext">With a total: more rows exist after this page. Without one: this page is full.</param>
public sealed record ModuleLookupResult(IReadOnlyList<ModuleLookupItem> Items, int? Total, int Skip, int Take, bool HasNext);

public interface IModuleLookupService
{
    /// <summary>
    /// The choices of a select / browse field of a published module, read through the field's lookup data source (a Query;
    /// a Command data source is refused). The data source sees a fresh context: the request's claims and clock, plus
    /// Skip / Take / Search / IsCount as data source conventions; never the request's query string, form or route.
    /// </summary>
    /// <exception cref="ModuleNotFoundException">No such module, deleted, or still a draft.</exception>
    /// <exception cref="ModuleRecordUnavailableException">
    /// The module has no select / browse field with that name (<c>NotConfigured</c>), or the lookup cannot run (data source
    /// gone, switched off, wrong statement type or mapping, a configured column missing from its result).
    /// </exception>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleLookupResult> SearchAsync(
        Guid moduleId, string fieldName, ParameterContext context, ModuleLookupRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// The choices of a select filter (display.filters) that reads them from a lookup data source; same rules as
    /// <see cref="SearchAsync"/> (a Query, a fresh context, a select's 200 choices).
    /// </summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleRecordUnavailableException">No such select filter with a lookup (<c>NotConfigured</c>), or it cannot run.</exception>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleLookupResult> SearchFilterAsync(
        Guid moduleId, string filterName, ParameterContext context, ModuleLookupRequest? request = null, CancellationToken ct = default);
}

public sealed class ModuleLookupService(
    IModuleRepository modules, IDataSourceService dataSources, ILogger<ModuleLookupService> logger) : IModuleLookupService
{
    public const int SelectDefaultTake = 200;
    public const int SelectMaxTake = 200;
    public const int BrowseDefaultTake = 10;
    public const int BrowseMaxTake = 50;
    public const int MaxSearchLength = 255;
    private const int MaxFieldNameLength = 128;

    public async Task<ModuleLookupResult> SearchAsync(
        Guid moduleId, string fieldName, ParameterContext context, ModuleLookupRequest? request = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var module = await GetPublishedAsync(moduleId, ct);

        var name = fieldName?.Trim() ?? "";
        var field = name.Length is > 0 and <= MaxFieldNameLength
            ? module.Config.Form.DataFields.FirstOrDefault(f =>
                ModuleFieldValue.IsLookup(f.Type) && f.Lookup is not null
                && string.Equals(f.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (field?.Lookup is not { } lookup || lookup.DataSourceId == Guid.Empty)
            throw new ModuleRecordUnavailableException(
                "Pilihan ini tidak tersedia.", $"the module has no select/browse field '{name}' with a lookup", notConfigured: true);

        return await RunAsync(module, name, lookup, field.Type == FormFieldType.Browse, context, request ?? new ModuleLookupRequest(), ct);
    }

    public async Task<ModuleLookupResult> SearchFilterAsync(
        Guid moduleId, string filterName, ParameterContext context, ModuleLookupRequest? request = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var module = await GetPublishedAsync(moduleId, ct);

        var name = filterName?.Trim() ?? "";
        var filter = name.Length is > 0 and <= MaxFieldNameLength
            ? module.Config.Display.Filters.FirstOrDefault(f =>
                f.Type == FormFieldType.Select && f.Lookup is not null && string.Equals(f.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (filter?.Lookup is not { } lookup || lookup.DataSourceId == Guid.Empty)
            throw new ModuleRecordUnavailableException(
                "Pilihan ini tidak tersedia.", $"the module has no select filter '{name}' with a lookup", notConfigured: true);

        return await RunAsync(module, "filter:" + name, lookup, browse: false, context, request ?? new ModuleLookupRequest(), ct);
    }

    private async Task<Module> GetPublishedAsync(Guid moduleId, CancellationToken ct)
    {
        var module = await modules.GetAsync(moduleId, ct);
        if (module is null || module.Status != ModuleStatus.Published) throw new ModuleNotFoundException(moduleId);
        return module;
    }

    private async Task<ModuleLookupResult> RunAsync(
        Module module, string name, ModuleLookup lookup, bool browse, ParameterContext context, ModuleLookupRequest request, CancellationToken ct)
    {
        var maxTake = browse ? BrowseMaxTake : SelectMaxTake;
        var take = Math.Clamp(request.Take ?? (browse ? BrowseDefaultTake : SelectDefaultTake), 1, maxTake);
        var skip = Math.Max(request.Skip ?? 0, 0);
        var search = request.Search?.Trim();
        if (search is { Length: > MaxSearchLength }) search = search[..MaxSearchLength];

        DataSourceResult result;
        try
        {
            result = await dataSources.ExecuteAsync(
                lookup.DataSourceId, ModuleContexts.Isolate(context, new Dictionary<string, string?>(), null),
                new DataSourceRunRequest(skip, take, Search: search, IncludeTotal: true, RequireQuery: true), ct);
        }
        catch (Exception ex) when (ex is DataSourceNotFoundException or DataSourceForbiddenException or ParameterResolutionException
            or TableDefinitionException or SqlTemplateException or SchemaAccessDeniedException or SchemaObjectNotFoundException
            or ArgumentException or InvalidOperationException)
        {
            // Nothing the visitor sent reaches the data source's parameters, so a bad value here is a set-up problem.
            throw Unavailable(module, name, ex.GetType().Name + ": " + ex.Message, ex);
        }

        var list = result.List ?? throw Unavailable(module, name, "the lookup data source returned no result set at the list position", null);
        var valueIndex = IndexOf(list.Columns, lookup.ValueField.Trim());
        var textIndex = IndexOf(list.Columns, lookup.TextField.Trim());
        if (valueIndex < 0 || textIndex < 0)
            throw Unavailable(module, name,
                $"the lookup result has no column '{(valueIndex < 0 ? lookup.ValueField : lookup.TextField)}'", null);

        // A lookup that does not use @Skip / @Take returns every row on every call; its page is taken here (as for a module's list).
        var unpaged = list.Rows.Count > take || list.Truncated;
        var pageRows = (unpaged ? list.Rows.Skip(skip) : list.Rows).Take(take).ToList();

        var items = new List<ModuleLookupItem>();
        foreach (var row in pageRows)
        {
            var value = valueIndex < row.Count ? ModuleFieldValue.ToText(row[valueIndex]) : null;
            if (value is null) continue; // a choice without a value cannot be stored
            var text = textIndex < row.Count ? ModuleFieldValue.ToText(row[textIndex]) : null;
            items.Add(new ModuleLookupItem(value, string.IsNullOrWhiteSpace(text) ? value : text));
        }

        var totalCount = result.TotalCount ?? (unpaged && !list.Truncated ? list.Rows.Count : null);
        var hasNext = totalCount is { } total ? skip + pageRows.Count < total
            : unpaged ? list.Rows.Count > skip + pageRows.Count
            : list.Rows.Count >= take;
        return new ModuleLookupResult(items, totalCount, skip, take, hasNext);
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private ModuleRecordUnavailableException Unavailable(Module module, string field, string reason, Exception? inner)
    {
        logger.LogWarning(inner, "Module {ModuleId} ({ModuleName}) cannot run the lookup of field {Field}: {Reason}",
            module.Id, module.Name, field, reason);
        return new ModuleRecordUnavailableException("Pilihan ini tidak tersedia saat ini.", reason, notConfigured: false, inner);
    }
}
