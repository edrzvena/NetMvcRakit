using System.Net;

namespace Rakit.Web.Security;

/// <summary>
/// There is no login yet, so the Builder and Admin areas (which can read every table of the allowed schemas, run SQL in the
/// Playground and change the SP whitelist) are only served to requests from this machine unless
/// <c>Rakit:Security:BuilderLocalOnly</c> is false. Runtime pages (<c>/object/...</c>) and the home page stay open.
/// Behind a reverse proxy on the same machine every request looks local, so this is no substitute for a login there.
/// </summary>
public static class BuilderAccess
{
    public const string LocalOnlySetting = "Rakit:Security:BuilderLocalOnly";

    private static readonly string[] GuardedPrefixes = ["/Builder", "/Admin"];

    public static bool IsGuardedPath(PathString path)
        => GuardedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this request may use the Builder and Admin areas.</summary>
    public static bool IsAllowed(HttpContext http)
    {
        var localOnly = http.RequestServices.GetRequiredService<IConfiguration>().GetValue(LocalOnlySetting, true);
        return !localOnly || IsLocal(http.Connection);
    }

    private static bool IsLocal(ConnectionInfo connection)
        => connection.RemoteIpAddress is { } remote
            && (IPAddress.IsLoopback(remote) || (connection.LocalIpAddress is { } local && remote.Equals(local)));

    /// <summary>Answers 403 for a Builder / Admin request from another machine.</summary>
    public static IApplicationBuilder UseBuilderAccess(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (IsGuardedPath(http.Request.Path) && !IsAllowed(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync(
                "Builder dan Admin hanya bisa dibuka dari komputer server ini (belum ada login). " +
                "Atur Rakit:Security:BuilderLocalOnly = false hanya bila jaringan ini memang tertutup.");
            return;
        }
        await next();
    });

    /// <summary>Headers every response gets: no MIME sniffing, no framing by other sites (Page modules embed this site's own pages), no full referrer to other sites.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        http.Response.OnStarting(() =>
        {
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "SAMEORIGIN";
            headers.ContentSecurityPolicy = "frame-ancestors 'self'; object-src 'none'; base-uri 'self'";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });
        await next();
    });
}
