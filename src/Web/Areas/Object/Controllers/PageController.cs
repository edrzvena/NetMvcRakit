using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;
using Rakit.Core.Sql;
using Rakit.Web.Areas.Object.Models;

namespace Rakit.Web.Areas.Object.Controllers;

/// <summary>
/// Runtime page of a Table module (RUN-01): <c>/object/page/{moduleId}</c> is the shell, <c>.../data</c> the JSON the
/// grid loads. Only Published modules exist here; a draft looks exactly like a missing module.
/// The page's own query string is passed on to the data source (RUN-05); the grid's controls use the reserved
/// <c>_skip</c>, <c>_take</c>, <c>_order</c> and <c>_q</c> keys so they cannot collide with a data source's own parameters.
/// </summary>
[Area("Object")]
[Route("object/page")]
public class PageController(
    IModuleRuntimeService runtime, IModuleRecordService records, IOptions<RakitDbOptions> dbOptions, ILogger<PageController> logger)
    : Controller
{
    private const int MaxWriteBodyBytes = 262_144;

    private const string UnavailableView = "Unavailable";

    [HttpGet("{moduleId:guid}")]
    public async Task<IActionResult> Index(Guid moduleId, CancellationToken ct)
    {
        try
        {
            return View(await runtime.GetModuleAsync(moduleId, allowDraft: false, ct));
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

    [HttpGet("{moduleId:guid}/data")]
    public async Task<IActionResult> Data(Guid moduleId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var query = Request.Query;
            var result = await runtime.GetListAsync(
                moduleId, allowDraft: false, await HttpParameterContext.FromAsync(HttpContext, ct),
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

    private static ObjectActions ToActions(ModuleRecordActions actions) => new(actions.CanAdd, actions.CanEdit, actions.CanDelete);

    private JsonResult Error(int status, string message)
    {
        Response.StatusCode = status;
        return Json(new ObjectError(message));
    }

    /// <summary>Control values come from a URL: anything that is not a plain integer counts as "not given".</summary>
    private static int? ToInt(Microsoft.Extensions.Primitives.StringValues value)
        => int.TryParse(value.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
