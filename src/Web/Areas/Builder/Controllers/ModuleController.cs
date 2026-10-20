using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core;
using Rakit.Core.Config;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;
using Rakit.Core.Schema;
using Rakit.Core.Sql;
using Rakit.Web.Areas.Builder.Models;
using Rakit.Web.Areas.Object.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

[Area("Builder")]
public class ModuleController(
    IModuleRepository modules,
    IModuleGroupRepository groups,
    IDataSourceRepository dataSources,
    IDataSourceService dataSourceService,
    IModuleRuntimeService runtime,
    IModuleVisualService visual,
    IOptions<RakitDbOptions> dbOptions,
    ILogger<ModuleController> logger) : Controller
{
    private const string UnavailableView = "~/Areas/Builder/Views/Schema/Unavailable.cshtml";

    private string CurrentUser => User.Identity?.Name ?? "anonymous";

    /// <summary>Remarks to show with the success message (e.g. an embedded module that is still a draft).</summary>
    private readonly List<string> notes = [];

    private string WithNotes(string message) => notes.Count == 0 ? message : message + " " + string.Join(" ", notes);

    // ---- list ----------------------------------------------------------------------------------

    public Task<IActionResult> Index(CancellationToken ct) => GuardAsync(async () =>
        View(new ModuleIndexViewModel(await modules.ListAsync(ct: ct), TempData["Message"] as string, TempData["Error"] as string)));

    // ---- publish / unpublish / copy / delete (each is a POST from the list) -----------------------

    /// <summary>
    /// Makes the module visible at runtime. The stored configuration is re-validated by the repository; on top of that
    /// the list data source must still exist and be a Query data source (the module only stores its id).
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Publish(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var module = await modules.GetAsync(id, ct);
        if (module is null) return NotFound();

        var problem = (HasListSource(module.Type) ? await ListSourceProblemAsync(module.Config.Source.List?.DataSourceId, module.Type, ct) : null)
            ?? (await RecordSourceProblemsAsync(module.Config, ct)).FirstOrDefault();
        if (problem is not null)
        {
            TempData["Error"] = $"Module '{module.Name}' belum bisa dipublikasikan: {problem}";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await modules.SetStatusAsync(id, ModuleStatus.Published, CurrentUser, ct);
            TempData["Message"] = $"Module '{module.Name}' dipublikasikan: sekarang bisa dibuka di halaman runtime.";
        }
        catch (ModuleValidationException ex)
        {
            TempData["Error"] = $"Module '{module.Name}' belum bisa dipublikasikan: {string.Join(" ", ex.Errors)}";
        }
        return RedirectToAction(nameof(Index));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Unpublish(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        try
        {
            var module = await modules.SetStatusAsync(id, ModuleStatus.Draft, CurrentUser, ct);
            TempData["Message"] = $"Module '{module.Name}' dikembalikan ke draft: tidak lagi bisa dibuka di halaman runtime.";
        }
        catch (ModuleNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Copy(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        try
        {
            var copy = await modules.CopyAsync(id, CurrentUser, ct);
            TempData["Message"] = $"Disalin sebagai '{copy.Name}' (draft).";
        }
        catch (ModuleNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var module = await modules.GetAsync(id, ct);
        if (module is null) return NotFound();

        try
        {
            await modules.DeleteAsync(id, CurrentUser, ct);
            TempData["Message"] = $"Module '{module.Name}' dihapus.";
        }
        catch (ModuleInUseException ex)
        {
            TempData["Error"] = $"Module '{module.Name}' tidak bisa dihapus: masih dipakai {ex.MenuCount} menu. Hapus atau arahkan ulang menu itu dulu.";
        }
        catch (ModuleNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    });

    // ---- create --------------------------------------------------------------------------------

    [HttpGet]
    public Task<IActionResult> Create(CancellationToken ct) => GuardAsync(async () =>
        View("Form", await WithLookupsAsync(new ModuleFormViewModel(), ct)));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Create(ModuleFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        var input = await BuildInputAsync(model, ct);
        if (input is not null)
        {
            try
            {
                var created = await modules.CreateAsync(input, CurrentUser, ct);
                TempData["Message"] = WithNotes($"Module '{created.Name}' dibuat sebagai draft.");
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (AddModelErrors(ex)) { }
        }
        return View("Form", await WithLookupsAsync(model, ct));
    });

    // ---- edit ----------------------------------------------------------------------------------

    [HttpGet]
    public Task<IActionResult> Edit(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var module = await modules.GetAsync(id, ct);
        if (module is null) return NotFound();
        var model = new ModuleFormViewModel
        {
            Id = module.Id, Name = module.Name, Type = module.Type, Description = module.Description, GroupId = module.GroupId,
            DataSourceId = module.Config.Source.List?.DataSourceId, Status = module.Status,
            PageHtml = module.Config.Page?.Html,
            DetailDataSourceId = module.Config.Source.Detail?.DataSourceId,
            InsertDataSourceId = module.Config.Source.Insert?.DataSourceId,
            UpdateDataSourceId = module.Config.Source.Update?.DataSourceId,
            DeleteDataSourceId = module.Config.Source.Delete?.DataSourceId,
            ConfigJson = ModuleJson.Serialize(module.Config),
        };
        return View("Form", await WithLookupsAsync(model, ct));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(Guid id, ModuleFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        model.Id = id;
        var existing = await modules.GetAsync(id, ct);
        if (existing is null) return NotFound();
        model.Type = existing.Type; // the type is chosen at creation and never changes
        var input = await BuildInputAsync(model, ct);
        if (input is not null)
        {
            try
            {
                var updated = await modules.UpdateAsync(id, input, CurrentUser, ct);
                TempData["Message"] = WithNotes($"Module '{updated.Name}' disimpan.");
                return RedirectToAction(nameof(Index));
            }
            catch (ModuleNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex) when (AddModelErrors(ex)) { }
        }
        return View("Form", await WithLookupsAsync(model, ct));
    });

    // ---- helper endpoints for the form (builder screens: reasons are shown in full) --------------

    /// <summary>Columns a data source returns, to fill the Display tab. Runs through the data source preview path (rolled back, max 50 rows).</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> LoadColumns([FromBody] ModuleLoadColumnsRequest request, CancellationToken ct) => BuilderJsonAsync(async () =>
    {
        var context = string.IsNullOrWhiteSpace(request.Key)
            ? new ParameterContext()
            : new ParameterContext { Route = new Dictionary<string, string?> { ["id"] = request.Key.Trim() } };
        var result = await dataSourceService.ExecuteForPreviewAsync(
            request.DataSourceId, context, new DataSourceRunRequest(0, 1, IncludeTotal: false), ct);
        var list = result.List ?? throw new ModuleListUnavailableException(
            "Data source tidak mengembalikan daftar.", "the data source returned no result set at the list position");

        var first = list.Rows.FirstOrDefault();
        var columns = list.Columns.Select((name, i) =>
            new LoadedColumn(name, ModuleDisplayText.SuggestFormat(first is not null && i < first.Count ? first[i] : null)));
        return Json(new { columns, hasRows = first is not null });
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Preview([FromBody] ModulePreviewRequest request, CancellationToken ct) => BuilderJsonAsync(async () =>
    {
        ModuleConfig config;
        try
        {
            config = request.Config.ValueKind == JsonValueKind.Object ? ModuleJson.Deserialize(request.Config.GetRawText()) : new ModuleConfig();
        }
        catch (JsonException)
        {
            return JsonError(400, "Config tidak dapat dibaca.");
        }
        // The preview shows the list only: record actions and the half-edited form must not block it.
        config.Source = new ModuleSource
        {
            List = request.DataSourceId is { } d ? new DataSourceRef { DataSourceId = d } : null,
        };
        config.Form = new ModuleForm();
        config.Display.KeyField = null;

        var input = new ModuleInput
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "preview" : request.Name.Trim(),
            Type = ModuleType.Table,
            Config = config,
        };
        var result = await runtime.PreviewListAsync(
            input, QueryContext(request.Query), new ModuleListRequest(request.Skip, request.Take, request.OrderBy, request.Search), ct);

        return Json(new ModulePreviewResponse(
            result.Columns.Select(c => new PreviewColumn(c.Field, c.Label, c.Sortable, ModuleDisplayText.FormatName(c.Format))).ToList(),
            result.Rows.Select(r => (IReadOnlyList<string?>)r
                .Select((value, i) => ModuleCellFormatter.Format(value, result.Columns[i].Format)).ToList()).ToList(),
            result.TotalCount, result.Skip, result.Take, result.HasNext, result.OrderBy));
    });

    /// <summary>
    /// Preview of a Widget (later also Chart) module that is not saved: the form's own state, run through the data source's
    /// preview path, answered with the same JSON the runtime page loads so the browser draws it with the same renderer.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> PreviewVisual([FromBody] VisualPreviewRequest request, CancellationToken ct) => BuilderJsonAsync(async () =>
    {
        ModuleConfig config;
        try
        {
            config = request.Config.ValueKind == JsonValueKind.Object ? ModuleJson.Deserialize(request.Config.GetRawText()) : new ModuleConfig();
        }
        catch (JsonException)
        {
            return JsonError(400, "Config tidak dapat dibaca.");
        }
        if (!Enum.TryParse<ModuleType>(request.Type, ignoreCase: true, out var type) || type != ModuleType.Widget)
            return JsonError(400, "Preview ini hanya untuk module Widget.");

        config.Source = new ModuleSource { List = Ref(request.DataSourceId) };
        config.Form = new ModuleForm();
        config.Display = new ModuleDisplay();
        var input = new ModuleInput
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "preview" : request.Name.Trim(),
            Type = type,
            Config = config,
        };

        var result = await visual.PreviewWidgetAsync(input, QueryContext(request.Query), ct);
        return Json(ObjectVisualMapping.ToWidget(result));
    });

    /// <summary>
    /// Page module preview: the HTML as it will be stored (sanitized) with each embedded module shown as a labelled box
    /// (name, type, status). The browser shows it inside a sandboxed iframe without scripts.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> PreviewHtml([FromBody] PageHtmlPreviewRequest request, CancellationToken ct) => BuilderJsonAsync(async () =>
    {
        var raw = request.Html ?? "";
        if (raw.Length > ModulePage.MaxHtmlLength) return JsonError(400, $"Isi HTML maksimal {ModulePage.MaxHtmlLength} karakter.");

        var boxes = new Dictionary<Guid, string>();
        foreach (var id in ModulePageHtml.EmbeddedModuleIds(raw).Take(ModulePageHtml.MaxEmbeds))
        {
            var target = await modules.GetAsync(id, ct);
            var text = target is null ? $"Module tidak ditemukan: {id}"
                : $"Module tersemat: {target.Name} ({ModuleDisplayText.TypeLabel(target.Type)}, {ModuleDisplayText.StatusLabel(target.Status)})";
            boxes[id] = "<div class=\"border rounded p-2 mb-3 bg-light text-muted small\">" + System.Net.WebUtility.HtmlEncode(text) + "</div>";
        }
        var html = ModulePageHtml.Render(raw, id => boxes.GetValueOrDefault(id, ""));
        return Json(new { html, changed = ModulePageHtml.Sanitize(raw).Trim() != raw.Trim() });
    });

    // ---- internals -----------------------------------------------------------------------------

    /// <summary>Turns "id=2&amp;q=abc" into the query-string part of a parameter context.</summary>
    private static ParameterContext QueryContext(string? query)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(query))
            foreach (var (key, value) in QueryHelpers.ParseQuery(query.TrimStart('?')))
            {
                var first = value.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (first is not null) values.TryAdd(key, first);
            }
        return new ParameterContext { QueryString = values };
    }

    /// <summary>
    /// Validates the posted form and returns the module input, or null with the errors added to ModelState.
    /// A new group is created last, once everything else is known to be valid.
    /// </summary>
    private async Task<ModuleInput?> BuildInputAsync(ModuleFormViewModel model, CancellationToken ct)
    {
        ModuleConfig config;
        try
        {
            config = ModuleJson.Deserialize(string.IsNullOrWhiteSpace(model.ConfigJson) ? "{}" : model.ConfigJson);
        }
        catch (JsonException)
        {
            ModelState.AddModelError("", "Config tidak dapat dibaca. Muat ulang halaman lalu coba lagi.");
            return null;
        }

        // Every data source comes from the selects (never from ConfigJson); Display, Form and Access come from ConfigJson.
        // A Form module only inserts, so only the Insert select counts for it.
        // A Page module has no data sources, form or display: only its HTML.
        config.Source = model.Type switch
        {
            ModuleType.Form => new ModuleSource { Insert = Ref(model.InsertDataSourceId) },
            ModuleType.Page => new ModuleSource(),
            ModuleType.Widget or ModuleType.Chart => new ModuleSource { List = Ref(model.DataSourceId) },
            _ => new ModuleSource
            {
                List = Ref(model.DataSourceId),
                Detail = Ref(model.DetailDataSourceId),
                Insert = Ref(model.InsertDataSourceId),
                Update = Ref(model.UpdateDataSourceId),
                Delete = Ref(model.DeleteDataSourceId),
            },
        };
        if (model.Type == ModuleType.Page)
        {
            config.Page = new ModulePage { Html = model.PageHtml ?? "" };
            config.Form = new ModuleForm();
            config.Display = new ModuleDisplay();
        }
        if (model.Type is ModuleType.Widget or ModuleType.Chart)
        {
            // Only its own settings count: no record form, no table columns.
            config.Form = new ModuleForm();
            config.Display = new ModuleDisplay();
        }
        var input = new ModuleInput
        {
            Name = model.Name?.Trim() ?? "",
            Type = model.Type,
            Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(),
            GroupId = model.GroupId,
            Config = config,
        };

        var errors = ModuleInputValidator.Validate(input);
        foreach (var e in errors) ModelState.AddModelError("", e);
        if (errors.Count > 0) return null;

        // The module only stores the data source's id, so this is where a wrong or unsuitable one is caught.
        if (HasListSource(model.Type) && await ListSourceProblemAsync(model.DataSourceId, model.Type, ct) is { } problem)
        {
            ModelState.AddModelError("", problem);
            return null;
        }
        var recordProblems = await RecordSourceProblemsAsync(config, ct);
        foreach (var e in recordProblems) ModelState.AddModelError("", e);
        if (recordProblems.Count > 0) return null;

        if (model.Type == ModuleType.Page)
        {
            // What is stored is the sanitized HTML; the embedded modules are checked against the database.
            var embedProblems = await EmbedProblemsAsync(config.Page!.Html, model.Id, ct);
            foreach (var e in embedProblems) ModelState.AddModelError("", e);
            if (embedProblems.Count > 0) return null;

            config.Page.Html = ModulePageHtml.Sanitize(config.Page.Html);
            if (string.IsNullOrWhiteSpace(config.Page.Html))
            {
                ModelState.AddModelError("", "Isi HTML kosong setelah disanitasi: hanya markup yang tidak aman.");
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(model.NewGroupName))
        {
            try
            {
                input.GroupId = (await groups.CreateAsync(model.NewGroupName, CurrentUser, ct)).Id;
            }
            catch (ModuleGroupValidationException ex)
            {
                foreach (var e in ex.Errors) ModelState.AddModelError("", e);
                return null;
            }
            catch (DuplicateModuleGroupNameException)
            {
                ModelState.AddModelError("", $"Grup '{model.NewGroupName.Trim()}' sudah ada; pilih dari daftar grup.");
                return null;
            }
            model.GroupId = input.GroupId;
            model.NewGroupName = null;
        }

        return input;
    }

    /// <summary>
    /// Errors for the modules a Page embeds: at most <see cref="ModulePageHtml.MaxEmbeds"/>, each must exist, not be the page
    /// itself and not be a Page (no nesting). A Draft target is allowed (it shows nothing until published) and is only noted.
    /// </summary>
    private async Task<IReadOnlyList<string>> EmbedProblemsAsync(string html, Guid? selfId, CancellationToken ct)
    {
        var ids = ModulePageHtml.EmbeddedModuleIds(html);
        var errors = new List<string>();
        if (ids.Count > ModulePageHtml.MaxEmbeds)
            errors.Add($"Satu halaman maksimal menyematkan {ModulePageHtml.MaxEmbeds} module.");

        foreach (var id in ids.Take(ModulePageHtml.MaxEmbeds))
        {
            if (id == selfId) { errors.Add("Halaman tidak bisa menyematkan dirinya sendiri."); continue; }
            var target = await modules.GetAsync(id, ct);
            if (target is null) errors.Add($"Module yang disematkan tidak ditemukan: {id}.");
            else if (target.Type == ModuleType.Page) errors.Add($"Module '{target.Name}' bertipe Page; hanya module Table atau Form yang bisa disematkan.");
            else if (target.Status != ModuleStatus.Published) notes.Add($"Catatan: module '{target.Name}' yang disematkan masih Draft, tampil kosong sampai dipublikasikan.");
        }
        return errors;
    }

    /// <summary>Table, Chart and Widget modules read their data from a List data source; Form and Page modules do not.</summary>
    private static bool HasListSource(ModuleType type) => type is ModuleType.Table or ModuleType.Chart or ModuleType.Widget;

    private static DataSourceRef? Ref(Guid? id) => id is { } value ? new DataSourceRef { DataSourceId = value } : null;

    /// <summary>
    /// What is wrong with the Detail / Insert / Update / Delete data sources and with the lookup data sources of select /
    /// browse fields: gone, or the wrong statement type.
    /// </summary>
    private async Task<IReadOnlyList<string>> RecordSourceProblemsAsync(ModuleConfig config, CancellationToken ct)
    {
        var source = config.Source;
        var hasLookups = config.Form.Fields.Any(f => ModuleFieldValue.IsLookup(f.Type) && f.Lookup is not null);
        if (!hasLookups && source.Detail is null && source.Insert is null && source.Update is null && source.Delete is null) return [];

        var kinds = (await dataSources.ListAsync(ct)).ToDictionary(d => d.Id, d => d.StatementType);
        StatementType? KindOf(Guid id) => kinds.TryGetValue(id, out var kind) ? kind : null;
        return [.. ModuleInputValidator.ValidateRecordSourceKinds(source, KindOf),
                .. ModuleInputValidator.ValidateLookupSourceKinds(config.Form.Fields, KindOf)];
    }

    /// <summary>Why the data source cannot be a module's list source (gone, or a Command data source), or null when it is fine.</summary>
    private async Task<string?> ListSourceProblemAsync(Guid? dataSourceId, ModuleType type, CancellationToken ct)
    {
        if (dataSourceId is null || dataSourceId == Guid.Empty) return $"Module {ModuleDisplayText.TypeLabel(type)} membutuhkan data source untuk list.";

        var ds = await dataSources.GetAsync(dataSourceId.Value, ct);
        if (ds is null) return "Data source tidak ditemukan.";
        if (ds.StatementType != StatementType.Query)
            return $"Data source '{ds.Code}' bertipe Command; sumber list harus bertipe Query.";
        return null;
    }

    /// <summary>Exception filter used for its side effect: turns expected failures into form errors.</summary>
    private bool AddModelErrors(Exception ex)
    {
        switch (ex)
        {
            case ModuleValidationException v:
                foreach (var e in v.Errors) ModelState.AddModelError("", e);
                return true;
            default:
                return false;
        }
    }

    private async Task<ModuleFormViewModel> WithLookupsAsync(ModuleFormViewModel model, CancellationToken ct)
    {
        var allGroups = await groups.ListAsync(ct);
        model.Groups = allGroups.Where(g => g.IsActive || g.Id == model.GroupId).ToList();

        var allDataSources = await dataSources.ListAsync(ct);
        var queryDataSources = allDataSources.Where(d => d.StatementType == StatementType.Query).ToList();
        model.DataSources = queryDataSources
            .Select(d => new DataSourceOption(d.Id, d.Code, d.Name, ModuleDisplayText.ModeLabel(d.Mode)))
            .ToList();
        model.DataSourceUnavailable = HasListSource(model.Type) && model.DataSourceId is { } id && queryDataSources.All(d => d.Id != id);

        // A saved record-action id that is gone or has the wrong type is not in these lists; the view keeps it visible so saving reports it.
        model.CommandDataSources = allDataSources
            .Where(d => d.StatementType == StatementType.Command)
            .Select(d => new DataSourceOption(d.Id, d.Code, d.Name, ModuleDisplayText.ModeLabel(d.Mode)))
            .ToList();
        model.WriteEnabled = dbOptions.Value.AllowWrite;
        model.EmbeddableModules = (await modules.ListAsync(ct: ct)).Where(m => m.Type is ModuleType.Table or ModuleType.Form).ToList();

        if (model.IsEdit && model.Id is { } moduleId)
            model.Status = (await modules.GetAsync(moduleId, ct))?.Status ?? model.Status;
        return model;
    }

    private JsonResult JsonError(int status, string message, IReadOnlyList<string>? errors = null, string? detail = null, string? kind = null)
    {
        Response.StatusCode = status;
        return Json(new { message, errors, detail, kind });
    }

    /// <summary>Maps the failures a builder helper call can have to a status code and a message that explains them.</summary>
    private async Task<IActionResult> BuilderJsonAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ModuleValidationException ex)
        {
            return JsonError(400, "Module belum valid.", errors: ex.Errors);
        }
        catch (DataSourceNotFoundException)
        {
            return JsonError(404, "Data source tidak ditemukan.");
        }
        catch (DataSourceForbiddenException ex)
        {
            return JsonError(403, ex.Message);
        }
        catch (ModuleListUnavailableException ex)
        {
            return JsonError(422, "Data source tidak dapat dijalankan.", detail: ex.Reason);
        }
        catch (ModuleListRequestException ex)
        {
            return JsonError(400, ex.Message);
        }
        catch (Exception ex) when (ex is ParameterResolutionException or TableDefinitionException or SqlTemplateException or ArgumentException)
        {
            return JsonError(400, ExceptionMessages.WithoutParameterSuffix(ex));
        }
        catch (Exception ex) when (ex is SchemaAccessDeniedException or SchemaObjectNotFoundException)
        {
            return JsonError(400, "Tabel atau prosedur tidak ditemukan atau tidak diizinkan.");
        }
        catch (SqlExecutionException ex)
        {
            return JsonError(422, ex.Message, detail: ex.Detail, kind: ex.Kind.ToString());
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Module builder helper could not reach the database.");
            return JsonError(503, "Database tidak tersedia.");
        }
    }

    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Module screen could not reach the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(UnavailableView);
        }
    }
}
