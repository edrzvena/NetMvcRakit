using Microsoft.Extensions.Logging;
using Rakit.Core.DataSources;
using Rakit.Core.Workflows;

namespace Rakit.Core.Modules;

/// <param name="Message">Safe to show to end users (success text, or why it failed).</param>
/// <param name="Display">What the browser should show after a successful run (a page of HTML, or another module), or null.</param>
public sealed record ModuleActionResult(
    bool Success, WorkflowFailureKind Failure, string Message, IReadOnlyList<WorkflowStepResult> Steps, WorkflowDisplay? Display = null);

public interface IModuleActionService
{
    /// <summary>
    /// Runs the workflow behind a button of a published Table or Form module. A Form module's values are validated against its
    /// form fields first (like Insert) and passed on as FormField values; a Table module passes none. A per-row button needs
    /// the row's <paramref name="key"/> (RouteValue <c>id</c>). The request's query string, form and route values are never
    /// handed to the workflow. A failing step is reported in the result (not thrown).
    /// </summary>
    /// <exception cref="ModuleNotFoundException">No such module, deleted, or still a draft.</exception>
    /// <exception cref="ModuleRecordUnavailableException">
    /// No such button (<c>NotConfigured</c>), or its workflow is gone or not published.
    /// </exception>
    /// <exception cref="ModuleRecordValidationException">A form field is invalid, or the key is missing or too long.</exception>
    Task<ModuleActionResult> RunAsync(
        Guid moduleId, string actionCode, string? key, IReadOnlyDictionary<string, string?> values, ParameterContext context,
        CancellationToken ct = default);
}

public sealed class ModuleActionService(
    IModuleRepository modules, IWorkflowRunner runner, ILogger<ModuleActionService> logger) : IModuleActionService
{
    public async Task<ModuleActionResult> RunAsync(
        Guid moduleId, string actionCode, string? key, IReadOnlyDictionary<string, string?> values, ParameterContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);

        var module = await modules.GetAsync(moduleId, ct);
        if (module is null || module.Status != ModuleStatus.Published) throw new ModuleNotFoundException(moduleId);

        var code = actionCode?.Trim() ?? "";
        var action = module.Type is ModuleType.Table or ModuleType.Form
            ? module.Config.Actions.FirstOrDefault(a => string.Equals(a.Code.Trim(), code, StringComparison.OrdinalIgnoreCase))
            : null;
        if (action is null)
            throw new ModuleRecordUnavailableException(
                "Aksi ini tidak tersedia untuk halaman ini.", $"the module has no action '{code}'", notConfigured: true);

        string? recordKey = null;
        if (action.PerRow)
        {
            recordKey = key?.Trim() ?? "";
            if (recordKey.Length == 0 || recordKey.Length > ModuleRecordService.MaxKeyLength)
                throw new ModuleRecordValidationException(new Dictionary<string, string> { [""] = "Kunci data tidak valid." });
        }

        // Only a Form module has fields to validate; a Table button sends nothing but the row key.
        var form = module.Type == ModuleType.Form
            ? ModuleRecordService.ValidateFields(module, values, forInsert: true)
            : new Dictionary<string, string?>();

        WorkflowRunResult run;
        try
        {
            run = await runner.RunAsync(
                action.WorkflowId,
                new WorkflowRunContext(context, form, recordKey, module.Id, action.Code.Trim(), context.Claims.GetValueOrDefault("name")),
                ct);
        }
        catch (WorkflowNotFoundException ex)
        {
            logger.LogWarning("Module {ModuleId} ({ModuleName}) action {Action}: its workflow is gone or not published.", module.Id, module.Name, action.Code);
            throw new ModuleRecordUnavailableException(
                "Aksi ini tidak tersedia saat ini.", "the action's workflow is gone or not published", notConfigured: false, ex);
        }

        return run.Success
            ? new ModuleActionResult(true, WorkflowFailureKind.None, $"Aksi '{action.Label.Trim()}' berhasil dijalankan.", run.Steps, run.Display)
            : new ModuleActionResult(false, run.Failure, run.Message, run.Steps);
    }
}
