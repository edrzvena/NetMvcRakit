using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;
using Rakit.Core.Sql;
using Rakit.Core.Workflows;
using Rakit.Web.Areas.Object.Models;

namespace Rakit.Web.Areas.Object.Controllers;

/// <summary>
/// Runtime page of a module (RUN-01): <c>/object/page/{moduleId}</c> is the shell, <c>.../data</c> the JSON it loads (a Table's
/// grid page, a Chart's series or a Widget's value, by the module's type). Only Published modules exist here; a draft looks exactly like a missing module.
/// The page's own query string is passed on to the data source (RUN-05); the grid's controls use the reserved
/// <c>_skip</c>, <c>_take</c>, <c>_order</c> and <c>_q</c> keys so they cannot collide with a data source's own parameters.
/// </summary>
[Area("Object")]
[Route("object/page")]
public class PageController(
    IModuleRuntimeService runtime, IModuleRecordService records, IModuleLookupService lookups, IModuleVisualService visual,
    IModuleActionService actions, IModuleExportService exports, IModuleImportService imports,
    IOptions<RakitDbOptions> dbOptions, ILogger<PageController> logger)
    : Controller
{
    private const int MaxWriteBodyBytes = 262_144;

    private const string UnavailableView = "Unavailable";

    [HttpGet("{moduleId:guid}")]
    public async Task<IActionResult> Index(Guid moduleId, CancellationToken ct)
    {
        try
        {
            var module = await runtime.GetModuleAsync(moduleId, allowDraft: false, ct);
            ViewData["WriteEnabled"] = dbOptions.Value.AllowWrite;
            if (module.Type == ModuleType.Page)
            {
                // Sanitized again on every render; a page that is itself embedded (?embed=1) does not embed further, so there are no loops.
                var embedded = Request.Query["embed"] == "1";
                ViewData["PageHtml"] = new HtmlString(ModulePageHtml.Render(module.Config.Page?.Html, EmbedMarkup, drop: embedded));
                return View("HtmlPage", module);
            }
            return module.Type switch
            {
                ModuleType.Table => View(module),
                ModuleType.Form => View("FormPage", module),
                ModuleType.Chart => View("ChartPage", module),
                ModuleType.Widget => View("WidgetPage", module),
                _ => NotFound(),
            };
        }
        catch (ModuleNotFoundException)
        {
            return NotFound();
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Runtime page could not reach the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(UnavailableView);
        }
    }

    /// <summary>The iframe that shows another module inside a Page module; its height follows the content (page-embed.js).</summary>
    private string EmbedMarkup(Guid moduleId)
    {
        var url = Url.Action("Index", "Page", new { area = "Object", moduleId, embed = 1 }) ?? "";
        return "<div class=\"rk-embed mb-3\"><iframe data-rakit-embed class=\"w-100 border-0\" style=\"height: 320px\" loading=\"lazy\" title=\"Module tersemat\" src=\""
            + HtmlEncoder.Default.Encode(url) + "\"></iframe></div>";
    }

    [HttpGet("{moduleId:guid}/data")]
    public async Task<IActionResult> Data(Guid moduleId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            // The module's type decides what the JSON is: a Table grid page, a Chart's series or a Widget's value (all published only).
            var module = await runtime.GetModuleAsync(moduleId, allowDraft: false, ct);
            var context = await HttpParameterContext.FromAsync(HttpContext, ct);
            if (module.Type == ModuleType.Chart) return Json(ObjectVisualMapping.ToChart(await visual.GetChartAsync(module, context, ct)));
            if (module.Type == ModuleType.Widget) return Json(ObjectVisualMapping.ToWidget(await visual.GetWidgetAsync(module, context, ct)));

            var query = Request.Query;
            var result = await runtime.GetListAsync(
                module, context,
                new ModuleListRequest(ToInt(query["_skip"]), ToInt(query["_take"]), query["_order"].FirstOrDefault(), query["_q"].FirstOrDefault()), ct);

            var columns = result.Columns;
            return Json(new ObjectPageData(
                columns.Select(c => new ObjectColumn(c.Field, c.Label, c.Sortable, c.Format.ToString().ToLowerInvariant())).ToList(),
                result.Rows.Select(row => (IReadOnlyList<string?>)row
                    .Select((value, i) => ModuleCellFormatter.Format(value, columns[i].Format)).ToList()).ToList(),
                result.TotalCount, result.Skip, result.Take, result.Module.Config.Display.MaxTake,
                result.HasNext, result.OrderBy, result.Search, result.Keys,
                ToActions(ModuleRecordActions.For(result.Module.Config, dbOptions.Value.AllowWrite))));
        }
        catch (ModuleNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "Halaman tidak ditemukan.");
        }
        catch (ModuleListUnavailableException ex)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (ModuleListRequestException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (SqlExecutionException ex)
        {
            return SqlError(ex);
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Runtime page data could not reach the database.");
            return Error(StatusCodes.Status503ServiceUnavailable, "Database tidak tersedia saat ini.");
        }
    }

    // ---- record actions (Detail / Insert / Update / Delete) --------------------------------------------------
    // Writes are POST with the anti-forgery token (header RequestVerificationToken). Which keys and values reach a data
    // source is decided by ModuleRecordService from the module's configured form fields, never by this controller.

    /// <summary>
    /// Choices of a select / browse field: <c>_q</c> searches, <c>_skip</c> / <c>_take</c> page (a select returns up to 200
    /// choices, a browse up to 50 per page). Same error contract as the record endpoints.
    /// </summary>
    [HttpGet("{moduleId:guid}/lookup/{field}")]
    public Task<IActionResult> Lookup(Guid moduleId, string field, CancellationToken ct) => RecordAsync(async () =>
    {
        var query = Request.Query;
        var result = await lookups.SearchAsync(
            moduleId, field, await HttpParameterContext.FromAsync(HttpContext, ct),
            new ModuleLookupRequest(ToInt(query["_skip"]), ToInt(query["_take"]), query["_q"].FirstOrDefault()), ct);
        return Json(new ObjectLookup(
            result.Items.Select(i => new ObjectLookupItem(i.Value, i.Text)).ToList(), result.Total, result.Skip, result.Take, result.HasNext));
    });

    /// <summary>Choices of a select filter that reads them from a lookup data source (same shape as a field's lookup).</summary>
    [HttpGet("{moduleId:guid}/filter/{name}/lookup")]
    public Task<IActionResult> FilterLookup(Guid moduleId, string name, CancellationToken ct) => RecordAsync(async () =>
    {
        var query = Request.Query;
        var result = await lookups.SearchFilterAsync(
            moduleId, name, await HttpParameterContext.FromAsync(HttpContext, ct),
            new ModuleLookupRequest(ToInt(query["_skip"]), ToInt(query["_take"]), query["_q"].FirstOrDefault()), ct);
        return Json(new ObjectLookup(
            result.Items.Select(i => new ObjectLookupItem(i.Value, i.Text)).ToList(), result.Total, result.Skip, result.Take, result.HasNext));
    });

    /// <summary>The read-only "Lihat" modal: the record as label / text lines (needs display.buttons.detail).</summary>
    [HttpGet("{moduleId:guid}/record/{key}/view")]
    public Task<IActionResult> RecordView(Guid moduleId, string key, CancellationToken ct) => RecordAsync(async () =>
    {
        var items = await records.ViewAsync(moduleId, key, await HttpParameterContext.FromAsync(HttpContext, ct), ct);
        return Json(new ObjectRecordView(items.Select(i => new ObjectViewItem(i.Label, i.Text, i.IsSection)).ToList()));
    });

    // ---- export and import (CSV) -----------------------------------------------------------------

    /// <summary>
    /// A CSV of the list. The query string is the page's own (search, sort, paging, filters); <c>_all=1</c> exports every matching
    /// row (up to 10,000, header <c>X-Rakit-Export-Truncated: 1</c> when cut), otherwise only the page being shown.
    /// </summary>
    [HttpGet("{moduleId:guid}/export")]
    public Task<IActionResult> Export(Guid moduleId, CancellationToken ct) => RecordAsync(async () =>
    {
        var query = Request.Query;
        var result = await exports.ExportAsync(
            moduleId, await HttpParameterContext.FromAsync(HttpContext, ct),
            new ModuleListRequest(ToInt(query["_skip"]), ToInt(query["_take"]), query["_order"].FirstOrDefault(), query["_q"].FirstOrDefault()),
            all: query["_all"].FirstOrDefault() == "1", ct);
        Response.Headers["X-Rakit-Export-Rows"] = result.Rows.ToString(CultureInfo.InvariantCulture);
        if (result.Truncated) Response.Headers["X-Rakit-Export-Truncated"] = "1";
        Response.Headers.AccessControlExposeHeaders = "X-Rakit-Export-Rows, X-Rakit-Export-Truncated, Content-Disposition";
        return File(result.Content, "text/csv; charset=utf-8", result.FileName);
    });

    [HttpGet("{moduleId:guid}/import-template")]
    public Task<IActionResult> ImportTemplate(Guid moduleId, CancellationToken ct) => RecordAsync(async () =>
        File(ModuleImportService.WithBom(await imports.TemplateAsync(moduleId, ct)), "text/csv; charset=utf-8", "template-import.csv"));

    /// <summary>A CSV file (field "file", at most 1 MB): one Insert per row through the form's own validation; see <see cref="ObjectImportResult"/>.</summary>
    [HttpPost("{moduleId:guid}/import"), ValidateAntiForgeryToken, RequestSizeLimit(ModuleExport.MaxImportBytes + 65_536)]
    public Task<IActionResult> Import(Guid moduleId, CancellationToken ct) => RecordAsync(async () =>
    {
        var form = await Request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length == 0) throw new ModuleImportException("Pilih file CSV dulu.");
        if (file.Length > ModuleExport.MaxImportBytes) throw new ModuleImportException("File terlalu besar (maksimal 1 MB).");

        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync(ct);

        var result = await imports.ImportAsync(moduleId, text, await HttpParameterContext.FromAsync(HttpContext, ct), ct);
        return Json(new ObjectImportResult(
            result.Total, result.Imported, result.Failures.Select(f => new ObjectImportFailure(f.Row, f.Message)).ToList(), result.Notes));
    });

    [HttpGet("{moduleId:guid}/record/{key}")]
    public Task<IActionResult> RecordDetail(Guid moduleId, string key, CancellationToken ct) => RecordAsync(async () =>
    {
        var values = await records.GetAsync(moduleId, key, await HttpParameterContext.FromAsync(HttpContext, ct), ct);
        return Json(new ObjectRecord(values));
    });

    [HttpPost("{moduleId:guid}/record"), ValidateAntiForgeryToken, RequestSizeLimit(MaxWriteBodyBytes)]
    public Task<IActionResult> RecordInsert(Guid moduleId, CancellationToken ct) => RecordAsync(async () =>
    {
        var context = await HttpParameterContext.FromAsync(HttpContext, ct);
        await records.InsertAsync(moduleId, context.Form, context, ct);
        return Json(new ObjectDone("Data ditambahkan."));
    });

    [HttpPost("{moduleId:guid}/record/{key}"), ValidateAntiForgeryToken, RequestSizeLimit(MaxWriteBodyBytes)]
    public Task<IActionResult> RecordUpdate(Guid moduleId, string key, CancellationToken ct) => RecordAsync(async () =>
    {
        var context = await HttpParameterContext.FromAsync(HttpContext, ct);
        await records.UpdateAsync(moduleId, key, context.Form, context, ct);
        return Json(new ObjectDone("Data disimpan."));
    });

    [HttpPost("{moduleId:guid}/record/{key}/delete"), ValidateAntiForgeryToken, RequestSizeLimit(MaxWriteBodyBytes)]
    public Task<IActionResult> RecordDelete(Guid moduleId, string key, CancellationToken ct) => RecordAsync(async () =>
    {
        await records.DeleteAsync(moduleId, key, await HttpParameterContext.FromAsync(HttpContext, ct), ct);
        return Json(new ObjectDone("Data dihapus."));
    });

    // ---- workflow buttons ------------------------------------------------------------------------
    // POST with the anti-forgery token like the record writes. The button's workflow runs on the server; a Form module's
    // values are validated against its fields first (400 with per-field errors), a per-row button of a Table also names the row.
    // A step that fails is 422 (the database refused on purpose, or a procedure returned a non-zero code), 503 (set-up problem),
    // 504 (timeout) or 500, always with a text meant for end users.

    [HttpPost("{moduleId:guid}/action/{code}"), ValidateAntiForgeryToken, RequestSizeLimit(MaxWriteBodyBytes)]
    public Task<IActionResult> RunAction(Guid moduleId, string code, CancellationToken ct) => RunActionAsync(moduleId, code, null, ct);

    [HttpPost("{moduleId:guid}/action/{code}/{key}"), ValidateAntiForgeryToken, RequestSizeLimit(MaxWriteBodyBytes)]
    public Task<IActionResult> RunRowAction(Guid moduleId, string code, string key, CancellationToken ct) => RunActionAsync(moduleId, code, key, ct);

    private Task<IActionResult> RunActionAsync(Guid moduleId, string code, string? key, CancellationToken ct) => RecordAsync(async () =>
    {
        var context = await HttpParameterContext.FromAsync(HttpContext, ct);
        return ActionResponse(await actions.RunAsync(moduleId, code, key, context.Form, context, ct));
    });

    /// <summary>
    /// The visitor's answer to the form of an Action step (the run paused there and gave <c>runId</c>): the button pressed is
    /// <c>_button</c>, the rest are the field values. 400 = a value is invalid (the form stays open and the run keeps waiting),
    /// 404 = the form expired, was already answered, or belongs to another page; otherwise the same answers as an action. The
    /// answer can be another form.
    /// </summary>
    [HttpPost("{moduleId:guid}/resume/{runId:guid}"), ValidateAntiForgeryToken, RequestSizeLimit(MaxWriteBodyBytes)]
    public Task<IActionResult> ResumeAction(Guid moduleId, Guid runId, CancellationToken ct) => RecordAsync(async () =>
    {
        var context = await HttpParameterContext.FromAsync(HttpContext, ct);
        var button = context.Form.GetValueOrDefault("_button") ?? "";
        return ActionResponse(await actions.ResumeAsync(moduleId, runId, button, context.Form, ct));
    });

    private IActionResult ActionResponse(ModuleActionResult result)
    {
        if (result.Success)
            return Json(new ObjectActionDone(result.Message,
                result.Steps.Select(s => new ObjectActionStep(s.Code, s.Name, s.Status.ToString().ToLowerInvariant())).ToList(),
                result.Display is { } d ? ToDisplay(d) : null));

        return Error(result.Failure switch
        {
            WorkflowFailureKind.Business or WorkflowFailureKind.Rejected => StatusCodes.Status422UnprocessableEntity,
            WorkflowFailureKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            WorkflowFailureKind.Timeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError,
        }, result.Message);
    }

    private static ObjectActionDisplay ToDisplay(WorkflowDisplay d) => new(d.Kind, d.Html, d.Url, d.Prompt is { } p ? ToPrompt(p) : null);

    private static ObjectPrompt ToPrompt(WorkflowPrompt p) => new(
        p.RunId, p.Title, p.Intro,
        p.Fields.Select(f => new ObjectPromptField(
            f.Name.Trim(), string.IsNullOrWhiteSpace(f.Label) ? f.Name.Trim() : f.Label.Trim(),
            char.ToLowerInvariant(f.Type.ToString()[0]) + f.Type.ToString()[1..], f.Required && !f.Readonly, f.Default, f.Placeholder, f.Help, f.ColSize,
            f.Min, f.Max, f.Step, f.MaxLength,
            f.Options?.Select(o => new ObjectLookupItem(o.Value.Trim(), string.IsNullOrWhiteSpace(o.Text) ? o.Value.Trim() : o.Text.Trim())).ToList())).ToList(),
        p.Buttons.Select(b => new ObjectPromptButton(b.Code.Trim(), b.Label.Trim(), b.Style?.Trim().ToLowerInvariant(), b.Cancel)).ToList());

    /// <summary>
    /// Error contract of the record endpoints (all bodies are <see cref="ObjectError"/>): 400 invalid input (per-field
    /// <c>errors</c>), 404 no such page / action not configured / record not found, 422 the database refused on purpose
    /// (a THROW message, or "Operasi ditolak (kode N)" for a non-zero return value), 503 action or database unavailable,
    /// 504 timeout, 500 anything else. Only texts meant for end users are sent.
    /// </summary>
    private async Task<IActionResult> RecordAsync(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return await action();
        }
        catch (ModuleNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "Halaman tidak ditemukan.");
        }
        catch (ModuleRecordUnavailableException ex) when (ex.NotConfigured)
        {
            return Error(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (ModuleRecordUnavailableException ex)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (ModuleRecordNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "Data tidak ditemukan.");
        }
        catch (ModuleRecordValidationException ex)
        {
            var message = ex.Errors.Count == 1 && ex.Errors.TryGetValue("", out var whole) ? whole : "Data belum valid.";
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Json(new ObjectError(message, ex.Errors));
        }
        catch (ModuleRecordRejectedException ex)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
        }
        catch (ModuleImportException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (ModuleListRequestException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (ModuleListUnavailableException ex)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (SqlExecutionException ex)
        {
            return SqlError(ex);
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Runtime record action could not reach the database.");
            return Error(StatusCodes.Status503ServiceUnavailable, "Database tidak tersedia saat ini.");
        }
    }

    /// <summary>
    /// Business errors were raised on purpose by the SQL and are for the user; the others carry a generic text
    /// (the database's own message stays in Detail, which is never sent from here).
    /// </summary>
    private JsonResult SqlError(SqlExecutionException ex)
        => Error(ex.Kind == SqlExecErrorKind.Timeout ? StatusCodes.Status504GatewayTimeout
            : ex.Kind == SqlExecErrorKind.Business ? StatusCodes.Status422UnprocessableEntity
            : StatusCodes.Status500InternalServerError, ex.Message);

    private static ObjectActions ToActions(ModuleRecordActions actions) => new(actions.CanAdd, actions.CanEdit, actions.CanDelete, actions.CanView, actions.CanImport);

    private JsonResult Error(int status, string message)
    {
        Response.StatusCode = status;
        return Json(new ObjectError(message));
    }

    /// <summary>Control values come from a URL: anything that is not a plain integer counts as "not given".</summary>
    private static int? ToInt(Microsoft.Extensions.Primitives.StringValues value)
        => int.TryParse(value.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
