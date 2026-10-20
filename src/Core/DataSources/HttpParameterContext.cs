using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Rakit.Core.DataSources;

/// <summary>
/// Builds the <see cref="ParameterContext"/> a data source reads its parameter mappings from (RUN-05) out of the
/// current HTTP request. Entries with a blank value are left out, so a mapping's fallback <c>Value</c> applies
/// (an empty search box in a form is "no value", not an empty string).
/// </summary>
public static class HttpParameterContext
{
    public static async Task<ParameterContext> FromAsync(HttpContext http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var request = http.Request;

        return new ParameterContext
        {
            QueryString = Collect(request.Query.Select(kv => KeyValuePair.Create(kv.Key, FirstNonBlank(kv.Value)))),
            Form = Collect(await ReadFormAsync(request, ct)),
            Route = Collect(request.RouteValues.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.ToString()))),
            Claims = Collect((http.User?.Claims ?? []).Select(c => KeyValuePair.Create(c.Type, (string?)c.Value))),
            Now = DateTimeOffset.Now,
        };
    }

    private static async Task<IEnumerable<KeyValuePair<string, string?>>> ReadFormAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return [];
        try
        {
            var form = await request.ReadFormAsync(ct);
            return form.Select(kv => KeyValuePair.Create(kv.Key, FirstNonBlank(kv.Value))).ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return []; // unreadable body = no form values; the request itself is not this adapter's business
        }
    }

    private static string? FirstNonBlank(Microsoft.Extensions.Primitives.StringValues values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Case-insensitive; the first non-blank value per key wins.</summary>
    private static IReadOnlyDictionary<string, string?> Collect(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
            if (!string.IsNullOrWhiteSpace(value)) result.TryAdd(key, value);
        return result;
    }
}
