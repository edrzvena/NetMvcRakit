using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
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
public class PageController(IModuleRuntimeService runtime, ILogger<PageController> logger) : Controller
{
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
                result.HasNext, result.OrderBy, result.Search));
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
            // Business errors were raised on purpose by the SQL and are for the user; the others carry a generic text
            // (the database's own message stays in Detail, which is never sent from here).
            return Error(ex.Kind == SqlExecErrorKind.Timeout ? StatusCodes.Status504GatewayTimeout
                : ex.Kind == SqlExecErrorKind.Business ? StatusCodes.Status422UnprocessableEntity
                : StatusCodes.Status500InternalServerError, ex.Message);
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Runtime page data could not reach the database.");
            return Error(StatusCodes.Status503ServiceUnavailable, "Database tidak tersedia saat ini.");
        }
    }

    private JsonResult Error(int status, string message)
    {
        Response.StatusCode = status;
        return Json(new ObjectError(message));
    }

    /// <summary>Control values come from a URL: anything that is not a plain integer counts as "not given".</summary>
    private static int? ToInt(Microsoft.Extensions.Primitives.StringValues value)
        => int.TryParse(value.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
