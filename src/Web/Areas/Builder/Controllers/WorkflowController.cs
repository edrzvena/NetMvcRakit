using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;
using Rakit.Core.Workflows;
using Rakit.Web.Areas.Builder.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

[Area("Builder")]
public class WorkflowController(
    IWorkflowRepository workflows,
    IWorkflowChecker checker,
    IWorkflowScriptEngine scripts,
    IDataSourceRepository dataSources,
    IModuleRepository modules,
    ILogger<WorkflowController> logger) : Controller
{
    private const string UnavailableView = "~/Areas/Builder/Views/Schema/Unavailable.cshtml";
    private const int HistoryRows = 30;

    private string CurrentUser => User.Identity?.Name ?? "anonymous";

    // ---- list ----------------------------------------------------------------------------------

    public Task<IActionResult> Index(CancellationToken ct) => GuardAsync(async () =>
        View(new WorkflowIndexViewModel(await workflows.ListAsync(ct), TempData["Message"] as string, TempData["Error"] as string)));

    // ---- publish / unpublish / copy / delete (each is a POST from the list) -----------------------

    /// <summary>Makes the workflow runnable. Besides the structural check the repository does, every step's data source must exist and the outputs it reads must come from an earlier step.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Publish(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var workflow = await workflows.GetAsync(id, ct);
        if (workflow is null) return NotFound();

        var problems = (await checker.CheckAsync(workflow.Steps, workflow.Transaction, ct)).Where(i => i.IsError).Select(i => i.Message).ToList();
        if (problems.Count > 0)
        {
            TempData["Error"] = $"Workflow '{workflow.Name}' belum bisa dipublikasikan: {string.Join(" ", problems)}";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await workflows.SetStatusAsync(id, WorkflowStatus.Published, CurrentUser, ct);
            TempData["Message"] = $"Workflow '{workflow.Name}' dipublikasikan: tombol aksi yang memakainya sekarang bisa menjalankannya.";
        }
        catch (WorkflowValidationException ex)
        {
            TempData["Error"] = $"Workflow '{workflow.Name}' belum bisa dipublikasikan: {string.Join(" ", ex.Errors)}";
        }
        return RedirectToAction(nameof(Index));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Unpublish(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        try
        {
            var workflow = await workflows.SetStatusAsync(id, WorkflowStatus.Draft, CurrentUser, ct);
            TempData["Message"] = $"Workflow '{workflow.Name}' dikembalikan ke draft: tombol yang memakainya tidak bisa menjalankannya lagi.";
        }
        catch (WorkflowNotFoundException)
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
            var copy = await workflows.CopyAsync(id, CurrentUser, ct);
            TempData["Message"] = $"Disalin sebagai '{copy.Name}' (kode {copy.Code}, draft).";
        }
        catch (WorkflowNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var workflow = await workflows.GetAsync(id, ct);
        if (workflow is null) return NotFound();

        try
        {
            await workflows.DeleteAsync(id, CurrentUser, ct);
            TempData["Message"] = $"Workflow '{workflow.Name}' dihapus.";
        }
        catch (WorkflowInUseException ex)
        {
            TempData["Error"] = $"Workflow '{workflow.Name}' tidak bisa dihapus: masih dipakai {ex.ModuleCount} module. Lepaskan tombol aksinya dulu.";
        }
        catch (WorkflowNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    });

    // ---- create / edit -------------------------------------------------------------------------

    [HttpGet]
    public Task<IActionResult> Create(CancellationToken ct) => GuardAsync(async () =>
        View("Form", await WithLookupsAsync(new WorkflowFormViewModel(), ct)));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Create(WorkflowFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        var input = ToInput(model);
        if (input is not null)
        {
            try
            {
                var created = await workflows.CreateAsync(input, CurrentUser, ct);
                TempData["Message"] = $"Workflow '{created.Name}' dibuat sebagai draft.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex) when (AddModelErrors(ex)) { }
        }
        return View("Form", await WithLookupsAsync(model, ct));
    });

    [HttpGet]
    public Task<IActionResult> Edit(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var workflow = await workflows.GetAsync(id, ct);
        if (workflow is null) return NotFound();
        var model = new WorkflowFormViewModel
        {
            Id = workflow.Id, Code = workflow.Code, Name = workflow.Name, Description = workflow.Description, Status = workflow.Status,
            Transaction = workflow.Transaction,
            StepsJson = WorkflowJson.SerializeSteps(workflow.Steps),
        };
        return View("Form", await WithLookupsAsync(model, ct));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(Guid id, WorkflowFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        model.Id = id;
        var existing = await workflows.GetAsync(id, ct);
        if (existing is null) return NotFound();

        var input = ToInput(model);
        if (input is not null && existing.Status == WorkflowStatus.Published && !await PassesLiveCheckAsync(input, ct)) input = null;
        if (input is not null)
        {
            try
            {
                var updated = await workflows.UpdateAsync(id, input, CurrentUser, ct);
                TempData["Message"] = $"Workflow '{updated.Name}' disimpan.";
                return RedirectToAction(nameof(Index));
            }
            catch (WorkflowNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex) when (AddModelErrors(ex)) { }
        }
        return View("Form", await WithLookupsAsync(model, ct));
    });

    // ---- check (builder screen: reasons are shown in full) ---------------------------------------

    /// <summary>"Periksa": the form's own unsaved steps against the database and the flags. Nothing is executed.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Check([FromBody] JsonElement request, CancellationToken ct)
    {
        try
        {
            // read with the same JSON rules the steps are stored with (string enums)
            List<WorkflowStep> steps;
            var transaction = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("transaction", out var flag) && flag.ValueKind == JsonValueKind.True;
            try
            {
                steps = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("steps", out var raw)
                    ? WorkflowJson.DeserializeSteps(raw.GetRawText())
                    : [];
            }
            catch (JsonException)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Json(new { message = "Daftar langkah tidak dapat dibaca." });
            }

            var issues = WorkflowInputValidator.Validate(new WorkflowInput { Code = "check", Name = "check", Transaction = transaction, Steps = steps })
                .Select(e => new WorkflowIssue(true, e)).ToList();
            issues.AddRange(await checker.CheckAsync(steps, transaction, ct));
            return Json(new { issues = issues.Select(i => new { isError = i.IsError, message = i.Message }) });
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Workflow check could not reach the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Json(new { message = "Database tidak tersedia." });
        }
    }

    // ---- test a script (builder screen: the real reason is shown in full) -------------------------

    private const int MaxTestContextChars = 100_000;

    /// <summary>
    /// "Uji skrip": runs a Javascript step's source in the same sandbox and with the same limits as a real run, against the sample
    /// <c>ctx</c> the builder typed (JSON). Nothing is saved or written. The answer says how it went, with the detail a real run
    /// keeps from end users.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult TestScript([FromBody] JsonElement request)
    {
        var source = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        var context = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(source) || source.Length > ScriptStepConfig.MaxLength)
            return BadRequest(new { message = $"Skrip wajib diisi dan maksimal {ScriptStepConfig.MaxLength} karakter." });
        if (context.Length > MaxTestContextChars)
            return BadRequest(new { message = "Data contoh terlalu besar." });

        // the sample is JSON of an object; anything else would only fail inside the script with a confusing message
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(context) ? "{}" : context);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return BadRequest(new { message = "Data contoh harus berupa objek JSON." });
        }
        catch (JsonException ex)
        {
            return BadRequest(new { message = "Data contoh bukan JSON yang valid: " + ex.Message });
        }

        var result = scripts.Run(source, string.IsNullOrWhiteSpace(context) ? "{}" : context);
        return Json(new
        {
            ok = result.Ok,
            kind = result.Failure.ToString().ToLowerInvariant(),
            message = result.Message,
            detail = result.Detail,
            result = result.Json,
            durationMs = result.DurationMs,
        });
    }

    // ---- internals -----------------------------------------------------------------------------

    /// <summary>Reads the posted step list into an input, or returns null with the problem added to ModelState.</summary>
    private WorkflowInput? ToInput(WorkflowFormViewModel model)
    {
        try
        {
            return new WorkflowInput
            {
                Code = model.Code?.Trim() ?? "",
                Name = model.Name?.Trim() ?? "",
                Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(),
                Transaction = model.Transaction,
                Steps = WorkflowJson.DeserializeSteps(model.StepsJson),
            };
        }
        catch (JsonException)
        {
            ModelState.AddModelError("", "Daftar langkah tidak dapat dibaca. Muat ulang halaman lalu coba lagi.");
            return null;
        }
    }

    /// <summary>
    /// A published workflow is live, so saving it must pass the same checks as <see cref="Publish"/>; otherwise an edit could put
    /// a missing data source or a placeholder without a source step in front of visitors. Structural errors are left to the
    /// repository (it reports them the usual way).
    /// </summary>
    private async Task<bool> PassesLiveCheckAsync(WorkflowInput input, CancellationToken ct)
    {
        if (WorkflowInputValidator.Validate(input).Count > 0) return true;

        var errors = (await checker.CheckAsync(input.Steps, input.Transaction, ct)).Where(i => i.IsError).ToList();
        if (errors.Count == 0) return true;

        ModelState.AddModelError("", "Workflow ini sudah dipublikasikan, jadi perubahan hanya disimpan bila lolos pemeriksaan yang sama dengan Publish. Perbaiki dulu, atau kembalikan ke draft (Unpublish):");
        foreach (var issue in errors) ModelState.AddModelError("", issue.Message);
        return false;
    }

    /// <summary>Exception filter used for its side effect: turns expected failures into form errors.</summary>
    private bool AddModelErrors(Exception ex)
    {
        switch (ex)
        {
            case WorkflowValidationException v:
                foreach (var e in v.Errors) ModelState.AddModelError("", e);
                return true;
            case DuplicateWorkflowCodeException d:
                ModelState.AddModelError("", $"Kode workflow '{d.Code}' sudah dipakai.");
                return true;
            default:
                return false;
        }
    }

    private async Task<WorkflowFormViewModel> WithLookupsAsync(WorkflowFormViewModel model, CancellationToken ct)
    {
        model.DataSources = (await dataSources.ListAsync(ct))
            .OrderBy(d => d.Code)
            .Select(d => new StepSourceOption(d.Id,
                $"{d.Code} - {d.Name} ({DataSourceDisplay.ModeLabel(d.Mode)}, {(d.StatementType == StatementType.Command ? "Command" : "Query")})"))
            .ToList();

        model.Modules = (await modules.ListAsync(ct: ct))
            .Select(m => new StepSourceOption(m.Id, $"{m.Name} ({ModuleDisplayText.TypeLabel(m.Type)}{(m.Status == ModuleStatus.Published ? "" : ", draft")})"))
            .ToList();

        if (model.IsEdit && model.Id is { } id)
        {
            model.Status = (await workflows.GetAsync(id, ct))?.Status ?? model.Status;
            model.History = (await workflows.ListTrackingAsync(id, HistoryRows, ct))
                .Select(t => new TrackingRow(t.Created.ToLocalTime(), t.RunId, t.StepCode, t.StepName, t.Status, t.Message, t.DurationMs, t.ActionCode))
                .ToList();
        }
        return model;
    }

    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Workflow screen could not reach the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(UnavailableView);
        }
    }
}
