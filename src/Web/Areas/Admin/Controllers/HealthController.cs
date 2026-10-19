using Microsoft.AspNetCore.Mvc;
using Rakit.Core.Health;

namespace Rakit.Web.Areas.Admin.Controllers;

[Area("Admin")]
public class HealthController(IDbHealthService health) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
        => View(await health.CheckAsync(ct));
}
