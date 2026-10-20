using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rakit.Core.Modules;

namespace Rakit.Web.ViewComponents;

/// <summary>One navigation entry ready to draw: the address is resolved and the current page is marked.</summary>
public sealed record NavEntry(string Label, string? Href, bool IsDelimiter, bool IsActive, IReadOnlyList<NavEntry> Children, string? Icon = null);

/// <summary>
/// The dynamic part of the navbar, built from rakit.Menus. A broken or unreachable database must never break the page
/// around it, so a failure is logged and the menu is simply empty.
/// </summary>
public sealed class NavigationMenuViewComponent(IMenuNavigationService navigation, ILogger<NavigationMenuViewComponent> logger) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        IReadOnlyList<NavigationItem> items;
        try
        {
            items = await navigation.GetAsync(HttpContext.RequestAborted);
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "The navigation menu could not be read; showing the page without it.");
            items = [];
        }

        var current = HttpContext.Request.Path.Value ?? "";
        return View(items.Select(i => ToEntry(i, current)).ToList());
    }

    private NavEntry ToEntry(NavigationItem item, string currentPath)
    {
        var children = item.Children.Select(c => ToEntry(c, currentPath)).ToList();
        var href = item.IsDelimiter ? null
            : item.ModuleId is { } moduleId ? Url.Action("Index", "Page", new { area = "Object", moduleId })
            : item.Url;

        // A dropdown is active while one of its entries is; a plain item while its address is the current path.
        var active = children.Count > 0
            ? children.Any(c => c.IsActive)
            : href is not null && href.StartsWith('/') && string.Equals(href, currentPath, StringComparison.OrdinalIgnoreCase);
        return new NavEntry(item.Label, children.Count > 0 ? null : href, item.IsDelimiter, active, children, IconClass(item.Icon));
    }

    /// <summary>
    /// A Bootstrap Icons class from what a builder typed ("bi-people", "people", "bi bi-people"), or null when it is not a plain
    /// icon name. Only letters, digits and dashes get through, so the text can never become anything but a class name.
    /// </summary>
    private static string? IconClass(string? icon)
    {
        var text = icon?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.StartsWith("bi bi-", StringComparison.Ordinal)) text = text[3..];
        if (!text.StartsWith("bi-", StringComparison.Ordinal)) text = "bi-" + text;
        return System.Text.RegularExpressions.Regex.IsMatch(text, "^bi-[a-z0-9-]{1,50}$") ? text : null;
    }
}
