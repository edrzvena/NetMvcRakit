using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rakit.Core.Modules;
using Rakit.Web.Areas.Builder.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

[Area("Builder")]
public class MenuController(IMenuRepository menus, IModuleRepository modules, ILogger<MenuController> logger) : Controller
{
    private const string UnavailableView = "~/Areas/Builder/Views/Schema/Unavailable.cshtml";

    private string CurrentUser => User.Identity?.Name ?? "anonymous";

    // ---- list ----------------------------------------------------------------------------------

    public Task<IActionResult> Index(CancellationToken ct) => GuardAsync(async () =>
    {
        var items = await menus.ListAsync(ct);
        var moduleStatus = (await modules.ListAsync(ct: ct)).ToDictionary(m => m.Id);

        var rows = new List<MenuRow>();
        foreach (var root in items.Where(i => i.ParentId is null))
        {
            var rootRow = ToRow(root, 0, null, moduleStatus);
            var childRows = items.Where(i => i.ParentId == root.Id).Select(c => ToRow(c, 1, root, moduleStatus)).ToList();

            // The navbar drops a folder with nothing to show inside, so the list says so too.
            var isFolder = !root.IsDelimiter && root.ModuleId is null && root.Url is null;
            if (isFolder && rootRow.Hidden is null && !childRows.Any(c => !c.Item.IsDelimiter && c.Hidden is null))
                rootRow = rootRow with { Hidden = "belum ada item yang tampil di dalamnya" };

            rows.Add(rootRow);
            rows.AddRange(childRows);
        }
        return View(new MenuIndexViewModel(rows, TempData["Message"] as string, TempData["Error"] as string));
    });

    // ---- create --------------------------------------------------------------------------------

    [HttpGet]
    public Task<IActionResult> Create(Guid? parentId, CancellationToken ct) => GuardAsync(async () =>
        View("Form", await WithLookupsAsync(new MenuFormViewModel { ParentId = parentId }, ct)));

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Create(MenuFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        if (ChoiceError(model) is { } choiceError)
        {
            ModelState.AddModelError("", choiceError);
            return View("Form", await WithLookupsAsync(model, ct));
        }

        try
        {
            var created = await menus.CreateAsync(ToInput(model), CurrentUser, ct);
            TempData["Message"] = $"Menu '{Describe(created)}' dibuat.";
            return RedirectToAction(nameof(Index));
        }
        catch (MenuValidationException ex)
        {
            foreach (var e in ex.Errors) ModelState.AddModelError("", e);
        }
        return View("Form", await WithLookupsAsync(model, ct));
    });

    // ---- edit ----------------------------------------------------------------------------------

    [HttpGet]
    public Task<IActionResult> Edit(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var menu = await menus.GetAsync(id, ct);
        if (menu is null) return NotFound();
        var model = new MenuFormViewModel
        {
            Id = menu.Id, Label = menu.Label, Icon = menu.Icon, TargetType = MenuFormViewModel.TargetOf(menu),
            ModuleId = menu.ModuleId, Url = menu.Url, ParentId = menu.ParentId, IsActive = menu.IsActive, Order = menu.Order,
        };
        return View("Form", await WithLookupsAsync(model, ct));
    });

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Edit(Guid id, MenuFormViewModel model, CancellationToken ct) => GuardAsync(async () =>
    {
        model.Id = id;
        if (ChoiceError(model) is { } choiceError)
        {
            ModelState.AddModelError("", choiceError);
            return View("Form", await WithLookupsAsync(model, ct));
        }

        try
        {
            var updated = await menus.UpdateAsync(id, ToInput(model), CurrentUser, ct);
            TempData["Message"] = $"Menu '{Describe(updated)}' disimpan.";
            return RedirectToAction(nameof(Index));
        }
        catch (MenuNotFoundException)
        {
            return NotFound();
        }
        catch (MenuValidationException ex)
        {
            foreach (var e in ex.Errors) ModelState.AddModelError("", e);
        }
        return View("Form", await WithLookupsAsync(model, ct));
    });

    // ---- delete --------------------------------------------------------------------------------

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct) => GuardAsync(async () =>
    {
        var menu = await menus.GetAsync(id, ct);
        if (menu is null) return NotFound();

        try
        {
            await menus.DeleteAsync(id, CurrentUser, ct);
            TempData["Message"] = $"Menu '{Describe(menu)}' dihapus.";
        }
        catch (MenuHasChildrenException ex)
        {
            TempData["Error"] = $"Menu '{Describe(menu)}' tidak bisa dihapus: masih punya {ex.ChildCount} item di dalamnya. Hapus atau pindahkan item itu dulu.";
        }
        catch (MenuNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Index));
    });

    // ---- internals -----------------------------------------------------------------------------

    private static string Describe(Menu menu) => menu.IsDelimiter ? "(pemisah)" : menu.Label;

    /// <summary>Maps the form's target choice to the input; fields of the other choices are ignored.</summary>
    private static MenuInput ToInput(MenuFormViewModel m) => new()
    {
        Label = m.Label ?? "",
        Icon = m.Icon,
        IsDelimiter = m.TargetType == MenuTargetType.Delimiter,
        ModuleId = m.TargetType == MenuTargetType.Module ? m.ModuleId : null,
        Url = m.TargetType == MenuTargetType.Url ? m.Url : null,
        ParentId = m.ParentId,
        IsActive = m.IsActive,
        Order = m.Order,
    };

    /// <summary>
    /// A "module" or "URL" choice that was left empty: the repository would read it as a folder (an item without any
    /// target is legitimate), so the choice itself is checked here.
    /// </summary>
    private static string? ChoiceError(MenuFormViewModel m) => m.TargetType switch
    {
        MenuTargetType.Module when m.ModuleId is null => "Pilih module untuk menu ini.",
        MenuTargetType.Url when string.IsNullOrWhiteSpace(m.Url) => "Isi URL untuk menu ini.",
        _ => null,
    };

    private static MenuRow ToRow(Menu item, int depth, Menu? parent, IReadOnlyDictionary<Guid, ModuleSummary> moduleStatus)
    {
        string target;
        string? hidden = null;

        if (item.IsDelimiter) target = "pemisah";
        else if (item.ModuleId is { } moduleId)
        {
            if (moduleStatus.TryGetValue(moduleId, out var module))
            {
                target = $"Module: {module.Name}";
                if (module.Status != ModuleStatus.Published) hidden = "module masih draft";
            }
            else
            {
                target = "Module: (tidak ditemukan)";
                hidden = "module tidak ditemukan";
            }
        }
        else if (item.Url is not null) target = "URL: " + item.Url;
        else target = "folder";

        if (!item.IsActive) hidden = "nonaktif";
        else if (parent is { IsActive: false }) hidden = "induk nonaktif";
        return new MenuRow(item, depth, target, hidden);
    }

    private async Task<MenuFormViewModel> WithLookupsAsync(MenuFormViewModel model, CancellationToken ct)
    {
        model.Modules = (await modules.ListAsync(ct: ct))
            .Select(m => new MenuModuleOption(m.Id, m.Name + (m.Status == ModuleStatus.Published ? "" : " (draft)")))
            .ToList();

        var all = await menus.ListAsync(ct);
        model.HasChildren = model.Id is { } id && all.Any(m => m.ParentId == id);

        // Only top-level, non-delimiter items can be parents (two levels at most), and never the item itself.
        model.Parents = all
            .Where(m => m.ParentId is null && !m.IsDelimiter && m.Id != model.Id)
            .Select(m => new MenuParentOption(m.Id, m.Label))
            .ToList();
        return model;
    }

    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException)
        {
            return NotFound();
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Menu screen could not reach the database.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return View(UnavailableView);
        }
    }
}
