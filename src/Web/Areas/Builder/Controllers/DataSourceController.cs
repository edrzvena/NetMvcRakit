using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;
using Rakit.Web.Areas.Builder.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

[Area("Builder")]
public class DataSourceController(
    IDataSourceRepository repository,
    ISchemaBrowserService schema,
    IOptions<RakitDbOptions> options,
    ILogger<DataSourceController> logger) : Controller
{
    private const string UnavailableView = "~/Areas/Builder/Views/Schema/Unavailable.cshtml";

    private string CurrentUser => User.Identity?.Name ?? "anonymous";

    // ---- list / history ------------------------------------------------------------------------

    public Task<IActionResult> Index(CancellationToken ct) => GuardAsync(async () =>
        View(new DataSourceIndexViewModel(
            await repository.ListAsync(ct), options.Value.AllowRawQuery, options.Value.AllowWrite, TempData["Message"] as string)));

    public Task<IActionResult> Versions(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var current = await repository.GetAsync(id, ct);
        if (current is null) return NotFound();
        return View(new DataSourceVersionsViewModel(current, await repository.GetVersionsAsync(id, ct)));
    });

    // ---- create --------------------------------------------------------------------------------

    [HttpGet]
    public Task<IActionResult> Create(CancellationToken ct) => GuardAsync(async () =>
        View("Form", await WithLookupsAsync(new DataSourceFormViewModel(), ct)));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Create(DataSourceFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        if (TryBuildInput(model, out var input))
        {
            try
            {
                var created = await repository.CreateAsync(input, CurrentUser, ct);
                TempData["Message"] = $"Data source '{created.Code}' dibuat (versi {created.Version}).";
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
        var ds = await repository.GetAsync(id, ct);
        if (ds is null) return NotFound();
        var model = new DataSourceFormViewModel
        {
            Id = ds.Id, Code = ds.Code, Name = ds.Name, Mode = ds.Mode, StatementType = ds.StatementType,
            DefinitionJson = DataSourceJson.Serialize(ds.Definition), Version = ds.Version,
        };
        return View("Form", await WithLookupsAsync(model, ct));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(Guid id, DataSourceFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        model.Id = id;
        if (TryBuildInput(model, out var input))
        {
            try
            {
                var updated = await repository.UpdateAsync(id, input, CurrentUser, ct);
                TempData["Message"] = $"Data source '{updated.Code}' disimpan sebagai versi {updated.Version}.";
                return RedirectToAction(nameof(Index));
            }
            catch (DataSourceNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex) when (AddModelErrors(ex)) { }
        }
        return View("Form", await WithLookupsAsync(model, ct));
    });

    // ---- delete --------------------------------------------------------------------------------

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        try
        {
            await repository.DeleteAsync(id, CurrentUser, ct);
            TempData["Message"] = "Data source dihapus.";
        }
        catch (DataSourceNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    });

    // ---- helper endpoint for the form ----------------------------------------------------------

    /// <summary>Columns of an allowed table/view, for the column pickers in the form.</summary>
    [HttpGet]
    public Task<IActionResult> Columns(string? schemaName, string? name, CancellationToken ct) => GuardAsync(async () =>
    {
        var columns = await schema.GetColumnsAsync(schemaName!, name!, ct);
        return Json(columns.Select(c => new { name = c.Name, dataType = c.DataType }));
    });

    // ---- internals -----------------------------------------------------------------------------

    private bool TryBuildInput(DataSourceFormViewModel model, out DataSourceInput input)
    {
        input = new DataSourceInput();
        DataSourceDefinition definition;
        try
        {
            definition = DataSourceJson.Deserialize(string.IsNullOrWhiteSpace(model.DefinitionJson) ? "{}" : model.DefinitionJson);
        }
        catch (JsonException)
        {
            ModelState.AddModelError("", "Definisi tidak dapat dibaca. Muat ulang halaman lalu coba lagi.");
            return false;
        }

        input = new DataSourceInput
        {
            Code = model.Code?.Trim() ?? "",
            Name = model.Name?.Trim() ?? "",
            Mode = model.Mode,
            StatementType = model.Mode == DataSourceMode.Table ? StatementType.Query : model.StatementType,
            Definition = definition,
        };

        // Only the active mode's part is stored.
        if (input.Mode == DataSourceMode.Table) input.Definition.Query = null;
        else input.Definition.Table = null;

        var errors = DataSourceInputValidator.Validate(input);
        foreach (var e in errors) ModelState.AddModelError("", e);
        return errors.Count == 0;
    }

    /// <summary>Exception filter used for its side effect: turns expected failures into form errors.</summary>
    private bool AddModelErrors(Exception ex)
    {
        switch (ex)
        {
            case DataSourceValidationException v:
                foreach (var e in v.Errors) ModelState.AddModelError("", e);
                return true;
            case DuplicateDataSourceCodeException:
                ModelState.AddModelError(nameof(DataSourceFormViewModel.Code), "Code sudah dipakai data source lain.");
                return true;
            default:
                return false;
        }
    }

    private async Task<DataSourceFormViewModel> WithLookupsAsync(DataSourceFormViewModel model, CancellationToken ct)
    {
        model.AllowRawQuery = options.Value.AllowRawQuery;
        model.AllowWrite = options.Value.AllowWrite;
        try
        {
            model.Tables = await schema.GetTablesAsync(ct);
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Could not list tables for the data source form.");
            model.Tables = [];
            model.TablesUnavailable = true;
        }
        if (model.Id is { } id && model.Version is null)
            model.Version = (await repository.GetAsync(id, ct))?.Version;
        return model;
    }

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
            logger.LogError(ex, "Data source screen could not reach the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(UnavailableView);
        }
    }
}
