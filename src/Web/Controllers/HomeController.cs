using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;
using Rakit.Core.Workflows;
using Rakit.Web.Models;

namespace Rakit.Web.Controllers;

public class HomeController(
    IModuleRepository modules, IDataSourceRepository dataSources, IWorkflowRepository workflows, IMenuRepository menus,
    ILogger<HomeController> logger) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var allModules = await modules.ListAsync(ct: ct);
            var published = allModules.Where(m => m.Status == ModuleStatus.Published).ToList();
            return View(new HomeViewModel(
                published.Count, allModules.Count, (await dataSources.ListAsync(ct)).Count, (await workflows.ListAsync(ct)).Count,
                (await menus.ListAsync(ct)).Count, published.OrderBy(m => TypeOrder(m.Type)).ThenBy(m => m.Name).ToList(), DatabaseAvailable: true));
        }
        catch (SqlException ex)
        {
            // the home page must open even when the database does not: it is where a builder starts looking
            logger.LogError(ex, "The home page could not read its counts.");
            return View(new HomeViewModel(0, 0, 0, 0, 0, [], DatabaseAvailable: false));
        }
    }

    /// <summary>Pages first, then tables and forms, then the pieces (charts, widgets) a page is built from.</summary>
    private static int TypeOrder(ModuleType type) => type switch
    {
        ModuleType.Page => 0, ModuleType.Table => 1, ModuleType.Form => 2, ModuleType.Chart => 3, _ => 4,
    };

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
