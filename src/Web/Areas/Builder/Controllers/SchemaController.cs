using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rakit.Core.Schema;
using Rakit.Web.Areas.Builder.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

[Area("Builder")]
public class SchemaController(ISchemaBrowserService browser, ILogger<SchemaController> logger) : Controller
{
    public Task<IActionResult> Index(CancellationToken ct) => GuardAsync(async () =>
        View(new SchemaIndexViewModel(await browser.GetTablesAsync(ct), await browser.GetProceduresAsync(ct))));

    public Task<IActionResult> Table(string? schema, string? name, CancellationToken ct) => GuardAsync(async () =>
    {
        var columns = await browser.GetColumnsAsync(schema!, name!, ct);
        return View(new TableDetailViewModel(schema!.Trim(), name!.Trim(), columns));
    });

    public Task<IActionResult> Procedure(string? schema, string? name, CancellationToken ct) => GuardAsync(async () =>
        View(await browser.GetProcedureDetailAsync(schema!, name!, ct)));

    /// <summary>
    /// Bad input and anything outside the whitelist is a plain 404 (no hint which it was);
    /// an unreachable database is a generic 503. Exception text never reaches the response.
    /// </summary>
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is ArgumentException or SchemaAccessDeniedException or SchemaObjectNotFoundException)
        {
            return NotFound();
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Schema browser could not read database metadata.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View("Unavailable");
        }
    }
}
