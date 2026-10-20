using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core;
using Rakit.Core.Config;
using Rakit.Core.DataSources;
using Rakit.Core.Procedures;
using Rakit.Core.Schema;
using Rakit.Core.Sql;
using Rakit.Web.Areas.Builder.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

[Area("Builder")]
public class DataSourceController(
    IDataSourceRepository repository,
    IDataSourceService dataSources,
    ISchemaBrowserService schema,
    ISpWhitelistService whitelist,
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
        if (TryBuildInput(model, out var input) && await ProcedureAllowedAsync(input, ct))
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
        if (TryBuildInput(model, out var input) && await ProcedureAllowedAsync(input, ct))
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

    // ---- preview (builder screen: the database's own error text is returned in "detail") ---------

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview([FromBody] DataSourcePreviewRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<DataSourceMode>(request.Mode, ignoreCase: true, out var mode)
            || !Enum.TryParse<StatementType>(request.StatementType, ignoreCase: true, out var statement)
            || request.Definition.ValueKind != JsonValueKind.Object)
            return PreviewError(400, "Permintaan preview tidak valid.");

        DataSourceDefinition definition;
        try
        {
            definition = DataSourceJson.Deserialize(request.Definition.GetRawText());
        }
        catch (JsonException)
        {
            return PreviewError(400, "Definisi tidak dapat dibaca.");
        }

        // Same rule as saving: only the active mode's part counts.
        definition.KeepOnlyPartFor(mode);

        var samples = (request.Samples ?? [])
            .ToDictionary(kv => kv.Key, kv => string.IsNullOrEmpty(kv.Value) ? null : (object?)kv.Value, StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await dataSources.PreviewAsync(
                new DataSourceInput { Mode = mode, StatementType = statement, Definition = definition }, samples, ct);

            var isProcedure = mode == DataSourceMode.StoredProcedure;
            return Json(new PreviewResponse(
                result.ResultSets.Select(s => new PreviewResultSet(
                    s.Columns,
                    s.Rows.Select(r => (IReadOnlyList<string?>)r.Select(PreviewFormat.Cell).ToList()).ToList(),
                    s.Truncated)).ToList(),
                result.TotalCount,
                (long)result.Duration.TotalMilliseconds)
            {
                IsProcedure = isProcedure,
                ListResultSet = result.ListResultSetIndex,
                ReturnValue = result.ReturnValue,
                OutputParameters = result.OutputParameters.ToDictionary(kv => kv.Key, kv => PreviewFormat.Cell(kv.Value)),
            });
        }
        catch (DataSourceValidationException ex)
        {
            return PreviewError(400, "Definisi belum valid.", errors: ex.Errors);
        }
        catch (DataSourceForbiddenException ex)
        {
            return PreviewError(403, ex.Message);
        }
        catch (Exception ex) when (ex is TableDefinitionException or SqlTemplateException or ArgumentException)
        {
            return PreviewError(400, ExceptionMessages.WithoutParameterSuffix(ex));
        }
        catch (Exception ex) when (ex is SchemaAccessDeniedException or SchemaObjectNotFoundException)
        {
            return PreviewError(400, mode == DataSourceMode.StoredProcedure
                ? "Prosedur tidak ditemukan atau tidak diizinkan."
                : "Tabel tidak ditemukan atau tidak diizinkan.");
        }
        catch (SqlExecutionException ex)
        {
            return PreviewError(422, ex.Message, detail: ex.Detail, kind: ex.Kind.ToString());
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Preview could not reach the database.");
            return PreviewError(503, "Database tidak tersedia.");
        }
    }

    private JsonResult PreviewError(int status, string message, IReadOnlyList<string>? errors = null, string? detail = null, string? kind = null)
    {
        Response.StatusCode = status;
        return Json(new { message, errors, detail, kind });
    }

    // ---- helper endpoint for the form ----------------------------------------------------------

    /// <summary>Columns of an allowed table/view, for the column pickers in the form.</summary>
    [HttpGet]
    public Task<IActionResult> Columns(string? schemaName, string? name, CancellationToken ct) => GuardAsync(async () =>
    {
        var columns = await schema.GetColumnsAsync(schemaName!, name!, ct);
        return Json(columns.Select(c => new { name = c.Name, dataType = c.DataType }));
    });

    /// <summary>
    /// Parameters of a WHITELISTED procedure, for the stored procedure panel. Anything else (not listed, inactive,
    /// other schema, unknown) is a plain 404 without a hint which it was.
    /// </summary>
    [HttpGet]
    public Task<IActionResult> ProcedureParameters(string? schemaName, string? name, CancellationToken ct) => GuardAsync(async () =>
    {
        if (!await whitelist.IsAllowedAsync(schemaName ?? "", name ?? "", ct)) return NotFound();

        var parameters = await schema.GetProcedureParametersAsync(schemaName!, name!, ct);
        return Json(parameters.Select(p =>
        {
            var plain = p.Name.TrimStart('@');
            return new
            {
                name = plain,
                dataType = p.DataType,
                isOutput = p.IsOutput,
                hasDefault = p.HasDefault,
                isPaging = DataSourceService.PagingParameterNames.Contains(plain, StringComparer.OrdinalIgnoreCase),
            };
        }));
    });

    // ---- internals -----------------------------------------------------------------------------

    /// <summary>Save-time whitelist check (the run-time check in the data source service stays the authoritative one).</summary>
    private async Task<bool> ProcedureAllowedAsync(DataSourceInput input, CancellationToken ct)
    {
        if (input.Mode != DataSourceMode.StoredProcedure) return true;

        var proc = input.Definition.Procedure!; // the validator already required it
        if (await whitelist.IsAllowedAsync(proc.Schema, proc.Name, ct)) return true;

        ModelState.AddModelError("", $"Prosedur '{proc.Schema}.{proc.Name}' tidak ada di whitelist atau tidak aktif.");
        return false;
    }

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
        input.Definition.KeepOnlyPartFor(input.Mode);

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
        model.AllowStoredProcedure = options.Value.AllowStoredProcedure;
        try
        {
            model.Tables = await schema.GetTablesAsync(ct);

            var active = (await whitelist.ListAsync(ct)).Where(e => e.IsActive)
                .Select(e => $"{e.Schema}.{e.Name}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            model.Procedures = (await schema.GetProceduresAsync(ct)).Where(p => active.Contains($"{p.Schema}.{p.Name}")).ToList();
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Could not list tables and procedures for the data source form.");
            model.Tables = [];
            model.Procedures = [];
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
