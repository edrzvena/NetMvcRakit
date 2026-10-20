using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rakit.Core;
using Rakit.Core.Playground;
using Rakit.Core.Schema;
using Rakit.Web.Areas.Builder.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

/// <summary>
/// SQL Playground screen and its JSON endpoints. While Rakit:Playground:Enabled is false the screen explains how to switch it
/// on and every endpoint is a plain 404. Builder screen: the database's own error text is returned (never to end users).
/// </summary>
[Area("Builder")]
public class PlaygroundController(IPlaygroundService playground, ILogger<PlaygroundController> logger) : Controller
{
    public IActionResult Index()
    {
        var settings = playground.Settings;
        return settings.Enabled
            ? View(new PlaygroundIndexViewModel(settings, PlaygroundScripts.ColumnTypes))
            : View("Disabled");
    }

    [HttpGet]
    public Task<IActionResult> Tree(CancellationToken ct) => GuardAsync(async Task<IActionResult> () => Json(await playground.GetTreeAsync(ct)));

    [HttpGet]
    public Task<IActionResult> Object(string? schema, string? name, CancellationToken ct)
        => GuardAsync(async Task<IActionResult> () => Json(await playground.GetObjectAsync(schema ?? "", name ?? "", ct)));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Script([FromBody] PlaygroundScriptBody? body, CancellationToken ct) => GuardAsync(async Task<IActionResult> () =>
    {
        if (body is null) return Fail(400, "Permintaan tidak valid.");
        return Json(new { sql = await playground.ScriptAsync(body.Kind, body.Schema, body.Name, ct) });
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> NewTable([FromBody] NewTableSpec? spec) => GuardAsync(() =>
    {
        IActionResult result = spec is null
            ? Fail(400, "Permintaan tidak valid.")
            : Json(new { sql = playground.ScriptNewTable(spec) });
        return Task.FromResult(result);
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Run([FromBody] PlaygroundRunBody? body, CancellationToken ct) => GuardAsync(async Task<IActionResult> () =>
    {
        if (body is null) return Fail(400, "Permintaan tidak valid.");
        var result = await playground.RunAsync(new PlaygroundRunRequest(body.Sql ?? "", body.MaxRows, body.TimeoutSeconds, body.Rollback), ct);
        return Json(new PlaygroundRunResponse(
            result.Success, result.ResultSets, result.Messages, result.Error,
            (long)result.Duration.TotalMilliseconds, result.Committed, result.Write));
    });

    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PlaygroundDisabledException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is SchemaAccessDeniedException or SchemaObjectNotFoundException)
        {
            return NotFound();
        }
        catch (SqlBatchException ex)
        {
            return Fail(400, ex.Message, ex.Line);
        }
        catch (ArgumentException ex)
        {
            return Fail(400, ExceptionMessages.WithoutParameterSuffix(ex));
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Playground could not reach the database.");
            return Fail(503, "Database tidak tersedia.", detail: ex.Message);
        }
    }

    private JsonResult Fail(int status, string message, int? line = null, string? detail = null)
    {
        Response.StatusCode = status;
        return Json(new { message, line, detail });
    }
}
