using Microsoft.Extensions.Logging;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

namespace Rakit.Core.Modules;

/// <summary>
/// The module's data cannot be produced because of how it is set up (data source gone, switched off by a flag or the
/// whitelist, misconfigured mapping). The message is generic and safe for end users; the reason is logged.
/// </summary>
public sealed class ModuleListUnavailableException(string message, string reason, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Why, in plain words. For builder screens and logs only; never show it to end users.</summary>
    public string Reason { get; } = reason;
}

/// <summary>The visitor's request does not fit the data source (e.g. ?id=abc for an INT parameter). The message is safe to show.</summary>
public sealed class ModuleListRequestException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Skip">Rows to skip. Null = 0.</param>
/// <param name="Take">Page size. Null = the module's default; always clamped to 1..MaxTake.</param>
/// <param name="OrderBy"><c>field_asc</c> / <c>field_desc</c>; honoured only for a configured, sortable column.</param>
/// <param name="Search">Free text, trimmed and cut to 255 characters.</param>
public sealed record ModuleListRequest(int? Skip = null, int? Take = null, string? OrderBy = null, string? Search = null);

public sealed record ModuleListColumn(string Field, string Label, bool Sortable, ColumnFormat Format);

/// <param name="Rows">At most <paramref name="Take"/> rows, one cell per entry of <paramref name="Columns"/> (raw values, not yet formatted).</param>
/// <param name="TotalCount">Null when the data source cannot count.</param>
/// <param name="HasNext">With a total: more rows exist after this page. Without one: this page is full.</param>
/// <param name="OrderBy">The sort that was actually applied (canonical field spelling), or null.</param>
/// <param name="Keys">One raw key text per row (the <c>display.keyField</c> column, invariant culture; null = that row has no key),
/// only when the module has a Detail, Update or Delete source or a per-row workflow button, and the result has that column. Otherwise null.</param>
public sealed record ModuleListResult(
    Module Module,
    IReadOnlyList<ModuleListColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int? TotalCount,
    int Skip,
    int Take,
    bool HasNext,
    string? OrderBy,
    string? Search,
    IReadOnlyList<string?>? Keys = null);

public interface IModuleRuntimeService
{
    /// <summary>The module itself, for the page shell. Draft modules are only visible when <paramref name="allowDraft"/> (Builder preview).</summary>
    /// <exception cref="ModuleNotFoundException">No such module, deleted, or draft while drafts are not allowed (the two look the same on purpose).</exception>
    Task<Module> GetModuleAsync(Guid moduleId, bool allowDraft, CancellationToken ct = default);

    /// <summary>One page of a Table module's list, run through its data source with the request's parameters.</summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleListUnavailableException">The module's data source cannot run (generic message).</exception>
    /// <exception cref="ModuleListRequestException">A parameter value from the request does not fit.</exception>
    /// <exception cref="SqlExecutionException">The database rejected or timed out the call; <see cref="SqlExecutionException.Kind"/> says whether the message is for end users.</exception>
    Task<ModuleListResult> GetListAsync(
        Guid moduleId, bool allowDraft, ParameterContext context, ModuleListRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// Same as the overload by id, for a module the caller already loaded with <see cref="GetModuleAsync"/> (which has done the
    /// published / draft check), so a request that needs the module's type first does not read it twice.
    /// </summary>
    /// <exception cref="ModuleNotFoundException">The module is not a Table.</exception>
    Task<ModuleListResult> GetListAsync(
        Module module, ParameterContext context, ModuleListRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// Builder preview of a module that is not saved (yet): validates <paramref name="input"/>, then runs it like
    /// <see cref="GetListAsync"/>, except through the data source's preview path (Query only, at most 50 rows,
    /// stored procedures rolled back). The returned <see cref="ModuleListResult.Module"/> has an empty Id and status Draft.
    /// </summary>
    /// <exception cref="ModuleValidationException"/>
    /// <exception cref="ModuleListUnavailableException"/>
    /// <exception cref="ModuleListRequestException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<ModuleListResult> PreviewListAsync(
        ModuleInput input, ParameterContext context, ModuleListRequest? request = null, CancellationToken ct = default);
}

public sealed class ModuleRuntimeService(
    IModuleRepository modules, IDataSourceService dataSources, ILogger<ModuleRuntimeService> logger) : IModuleRuntimeService
{
    public const int MaxSearchLength = 255;

    public async Task<Module> GetModuleAsync(Guid moduleId, bool allowDraft, CancellationToken ct = default)
    {
        var module = await modules.GetAsync(moduleId, ct);
        if (module is null || (!allowDraft && module.Status != ModuleStatus.Published))
            throw new ModuleNotFoundException(moduleId);
        return module;
    }

    public async Task<ModuleListResult> GetListAsync(
        Guid moduleId, bool allowDraft, ParameterContext context, ModuleListRequest? request = null, CancellationToken ct = default)
    {
        var module = await GetModuleAsync(moduleId, allowDraft, ct);
        return await GetListAsync(module, context, request, ct);
    }

    public async Task<ModuleListResult> GetListAsync(
        Module module, ParameterContext context, ModuleListRequest? request = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.Type != ModuleType.Table) throw new ModuleNotFoundException(module.Id); // Form / Page / Chart / Widget modules have no list
        return await RunListAsync(module, context, request, preview: false, ct);
    }

    public async Task<ModuleListResult> PreviewListAsync(
        ModuleInput input, ParameterContext context, ModuleListRequest? request = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = ModuleInputValidator.Validate(input);
        if (errors.Count > 0) throw new ModuleValidationException(errors);

        var module = new Module(
            Guid.Empty, input.Name.Trim(), input.Type, input.Description, ModuleStatus.Draft, input.GroupId, input.Config,
            DateTime.UtcNow, null);
        return await RunListAsync(module, context, request, preview: true, ct);
    }

    /// <param name="preview">Builder preview: Query statements only, at most 50 rows, and a procedure runs in a rolled-back transaction.</param>
    private async Task<ModuleListResult> RunListAsync(
        Module module, ParameterContext context, ModuleListRequest? request, bool preview, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        request ??= new ModuleListRequest();

        var display = module.Config.Display;
        var dataSourceId = module.Config.Source.List?.DataSourceId;
        if (dataSourceId is null || dataSourceId == Guid.Empty)
            throw Unavailable(module, "the module has no list data source", null);

        var take = Math.Clamp(request.Take ?? display.Take, 1, Math.Clamp(display.MaxTake, 1, ModuleDisplay.HardMaxTake));
        var skip = Math.Max(request.Skip ?? 0, 0);
        var search = request.Search?.Trim();
        if (search is { Length: > MaxSearchLength }) search = search[..MaxSearchLength];
        var orderBy = ResolveOrder(request.OrderBy, display.Columns);

        DataSourceResult result;
        try
        {
            var run = new DataSourceRunRequest(skip, take, orderBy, search, IncludeTotal: true);
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

        var columns = BuildColumns(display.Columns, list.Columns, out var indexes);
        var rows = list.Rows.Take(take)
            .Select(row => (IReadOnlyList<object?>)indexes.Select(i => i >= 0 && i < row.Count ? row[i] : null).ToList())
            .ToList();

        var hasNext = result.TotalCount is { } total ? skip + rows.Count < total : list.Rows.Count >= take;
        return new ModuleListResult(module, columns, rows, result.TotalCount, skip, take, hasNext, orderBy, search,
            ReadKeys(module.Config, list, take));
    }

    private static List<string?>? ReadKeys(ModuleConfig config, DataTableResult list, int take)
    {
        var source = config.Source;
        var keyField = config.Display.KeyField?.Trim();
        var needsKeys = source.Detail is not null || source.Update is not null || source.Delete is not null
                        || config.Actions.Any(a => a.PerRow);
        if (string.IsNullOrEmpty(keyField) || !needsKeys) return null;

        var index = IndexOf(list.Columns, keyField);
        if (index < 0) return null;

        return list.Rows.Take(take)
            .Select(row => index < row.Count ? ModuleFieldValue.ToText(row[index]) : null)
            .ToList();
    }

    /// <summary>Only a configured, sortable column can be sorted on; anything else is ignored (it comes from a URL).</summary>
    private static string? ResolveOrder(string? requested, IReadOnlyList<ModuleColumn> columns)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var text = requested.Trim();
        var cut = text.LastIndexOf('_');
        if (cut <= 0) return null;

        var direction = text[(cut + 1)..].ToLowerInvariant();
        if (direction is not ("asc" or "desc")) return null;

        var field = text[..cut];
        var column = columns.FirstOrDefault(c => c.Sortable && string.Equals(c.Field.Trim(), field, StringComparison.OrdinalIgnoreCase));
        return column is null ? null : $"{column.Field.Trim()}_{direction}";
    }

    /// <summary>
    /// Configured visible columns in their configured order (a field the data source no longer returns stays as an
    /// empty column); with none configured, every result column as plain text.
    /// </summary>
    private static List<ModuleListColumn> BuildColumns(
        IReadOnlyList<ModuleColumn> configured, IReadOnlyList<string> resultColumns, out List<int> indexes)
    {
        var columns = new List<ModuleListColumn>();
        indexes = [];

        if (configured.Count == 0)
        {
            for (var i = 0; i < resultColumns.Count; i++)
            {
                columns.Add(new ModuleListColumn(resultColumns[i], resultColumns[i], false, ColumnFormat.Text));
                indexes.Add(i);
            }
            return columns;
        }

        foreach (var column in configured.Where(c => c.Visible))
        {
            var field = column.Field.Trim();
            columns.Add(new ModuleListColumn(
                field, string.IsNullOrWhiteSpace(column.Label) ? field : column.Label.Trim(), column.Sortable, column.Format));
            indexes.Add(IndexOf(resultColumns, field));
        }
        return columns;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private ModuleListUnavailableException Unavailable(Module module, string reason, Exception? inner)
    {
        logger.LogWarning(inner, "Module {ModuleId} ({ModuleName}) cannot run its list: {Reason}", module.Id, module.Name, reason);
        return new ModuleListUnavailableException("Data untuk halaman ini tidak tersedia saat ini.", reason, inner);
    }
}
