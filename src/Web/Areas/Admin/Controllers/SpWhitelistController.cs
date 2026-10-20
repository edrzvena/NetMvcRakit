using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Procedures;
using Rakit.Core.Schema;
using Rakit.Web.Areas.Admin.Models;

namespace Rakit.Web.Areas.Admin.Controllers;

[Area("Admin")]
public class SpWhitelistController(
    ISpWhitelistService whitelist,
    ISchemaBrowserService schema,
    IOptions<RakitDbOptions> options,
    ILogger<SpWhitelistController> logger) : Controller
{
    private const string UnavailableView = "~/Areas/Builder/Views/Schema/Unavailable.cshtml";

    private string CurrentUser => User.Identity?.Name ?? "anonymous";

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            // Configuration binding appends appsettings entries to the option's default array, so "app" can appear twice.
            var allowed = options.Value.AllowedSchemas.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rows = Merge(await schema.GetProceduresAsync(ct), await whitelist.ListAsync(ct), allowed);
            return View(new SpWhitelistIndexViewModel(rows, allowed, TempData["Message"] as string, TempData["Error"] as string));
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "SP whitelist page could not read the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(UnavailableView);
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(string? schemaName, string? name, bool active, CancellationToken ct)
    {
        try
        {
            var entry = await whitelist.SetActiveAsync(schemaName!, name!, active, CurrentUser, ct);
            TempData["Message"] = active
                ? $"Prosedur '{entry.Schema}.{entry.Name}' diaktifkan."
                : $"Prosedur '{entry.Schema}.{entry.Name}' dinonaktifkan. Data source yang memakainya tidak bisa dijalankan lagi.";
        }
        catch (Exception ex) when (ex is ArgumentException or SchemaAccessDeniedException or SchemaObjectNotFoundException)
        {
            // Bad input, a schema that is not allowed and a vanished procedure all look the same to the page.
            TempData["Error"] = "Prosedur tidak ditemukan atau tidak diizinkan.";
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "SP whitelist change could not reach the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(UnavailableView);
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Every procedure the catalog offers, plus listed rows whose procedure has disappeared.</summary>
    private static IReadOnlyList<SpWhitelistRow> Merge(
        IReadOnlyList<ProcedureInfo> discovered, IReadOnlyList<SpWhitelistEntry> entries, IReadOnlyList<string> allowedSchemas)
    {
        static string Key(string schema, string name) => $"{schema}.{name}".ToLowerInvariant();

        var listed = entries.ToDictionary(e => Key(e.Schema, e.Name));
        var rows = new List<SpWhitelistRow>();

        foreach (var proc in discovered)
        {
            var key = Key(proc.Schema, proc.Name);
            rows.Add(listed.Remove(key, out var entry)
                ? new SpWhitelistRow(proc.Schema, proc.Name, entry.IsActive ? SpWhitelistStatus.Active : SpWhitelistStatus.Inactive, entry.IsActive, entry.Updated, true)
                : new SpWhitelistRow(proc.Schema, proc.Name, SpWhitelistStatus.NotListed, false, null, true));
        }

        // Left over: listed, but not in the catalog any more. Only deactivating is possible, and only while the schema is allowed.
        foreach (var entry in listed.Values)
            rows.Add(new SpWhitelistRow(entry.Schema, entry.Name, SpWhitelistStatus.Missing, entry.IsActive, entry.Updated,
                CanToggle: entry.IsActive && allowedSchemas.Contains(entry.Schema, StringComparer.OrdinalIgnoreCase)));

        return rows
            .OrderBy(r => r.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
