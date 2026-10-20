using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Rakit.Core.Workflows;
using Rakit.Web.Areas.Builder.Models;

namespace Rakit.Web.Areas.Builder.Controllers;

/// <summary>
/// Builder > Tracking: how the workflows ran in a range of dates (server local time). The page is a shell; <c>Data</c> gives the
/// numbers, the per-day series and one page of runs, and <c>Run</c> the steps of one run. Read-only.
/// </summary>
[Area("Builder")]
public class TrackingController(
    ITrackingQueryService tracking, IWorkflowRepository workflows, ILogger<TrackingController> logger) : Controller
{
    private const int PageSize = 25;
    private const string DateFormat = "yyyy-MM-dd";
    private const string UnavailableView = "~/Areas/Builder/Views/Schema/Unavailable.cshtml";

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            return View(new TrackingIndexViewModel(
                (await workflows.ListAsync(ct)).OrderBy(w => w.Name).ToList(),
                today.AddDays(-6).ToString(DateFormat, CultureInfo.InvariantCulture), today.ToString(DateFormat, CultureInfo.InvariantCulture),
                TrackingQueryService.MaxDays));
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Tracking page could not reach the database.");
            return View(UnavailableView);
        }
    }

    /// <summary>
    /// <c>from</c> and <c>to</c> are dates (yyyy-MM-dd, both days included); <c>status</c> is success, failed, waiting or expired
    /// (blank = all). 400 for a date that cannot be read, a range that runs backwards, or one over the maximum number of days.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Data(string? from, string? to, Guid? workflowId, string? status, int page = 1, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (!DateOnly.TryParseExact(from, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
            || !DateOnly.TryParseExact(to, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
            return Problem400("Tanggal harus berbentuk yyyy-MM-dd.");
        if (last < first) return Problem400("Tanggal akhir tidak boleh sebelum tanggal awal.");
        if (last.DayNumber - first.DayNumber + 1 > TrackingQueryService.MaxDays) return Problem400($"Rentang maksimal {TrackingQueryService.MaxDays} hari.");

        TrackingRunStatus? wanted = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<TrackingRunStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Problem400("Status tidak dikenal.");
            wanted = parsed;
        }

        try
        {
            var zone = TimeZoneInfo.Local;
            var query = new TrackingQuery(
                TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(first.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone),
                TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(last.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone),
                workflowId, wanted);
            var overview = await tracking.QueryAsync(query, ct);

            var pageCount = Math.Max(1, (int)Math.Ceiling(overview.Runs.Count / (double)PageSize));
            var current = Math.Clamp(page, 1, pageCount);
            var rows = overview.Runs.Skip((current - 1) * PageSize).Take(PageSize).Select(r => new
            {
                runId = r.RunId,
                started = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.Started, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                workflow = r.WorkflowName,
                actionCode = r.ActionCode,
                runBy = r.RunBy,
                steps = r.StepCount,
                durationMs = r.DurationMs,
                status = r.Status.ToString().ToLowerInvariant(),
            }).ToList();

            var total = overview.Runs.Count;
            return Json(new
            {
                kpi = new { total, success = overview.Success, failed = overview.Failed, waiting = overview.Waiting, expired = overview.Expired, averageMs = overview.AverageDurationMs },
                truncated = overview.Truncated,
                maxRuns = TrackingQueryService.MaxRuns,
                days = overview.Days.Select(d => new { date = d.Date.ToString(DateFormat, CultureInfo.InvariantCulture), success = d.Success, failed = d.Failed, waiting = d.Waiting }),
                runs = rows,
                page = current,
                pageCount,
                pageSize = PageSize,
            });
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Tracking data could not be read.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Json(new { message = "Database tidak tersedia." });
        }
    }

    /// <summary>The rows of one run, oldest first (a step that waited for the visitor shows its waiting row and its answered row).</summary>
    [HttpGet]
    public async Task<IActionResult> Run(Guid runId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var zone = TimeZoneInfo.Local;
            var rows = await tracking.GetRunAsync(runId, ct);
            if (rows.Count == 0)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Json(new { message = "Run tidak ditemukan." });
            }
            return Json(new
            {
                steps = rows.Select(r => new
                {
                    code = r.StepCode,
                    name = string.IsNullOrWhiteSpace(r.StepName) ? r.StepCode : r.StepName,
                    status = r.Status.ToString().ToLowerInvariant(),
                    label = WorkflowDisplayText.TrackingLabel(r.Status),
                    message = r.Message,
                    durationMs = r.DurationMs,
                    time = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.Created, DateTimeKind.Utc), zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                }),
            });
        }
        catch (SqlException ex)
        {
            logger.LogError(ex, "Tracking run could not be read.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Json(new { message = "Database tidak tersedia." });
        }
    }

    private JsonResult Problem400(string message)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return Json(new { message });
    }
}
