using Rakit.Core.DataSources;

namespace Rakit.Core.Modules;

internal static class ModuleContexts
{
    public const string RouteKey = "id";

    /// <summary>
    /// A fresh context for a data source call made on behalf of a module: only the given form values and record key.
    /// The request's query string, route values and form keys are not passed on; its claims and clock are.
    /// </summary>
    public static ParameterContext Isolate(ParameterContext request, Dictionary<string, string?> form, string? key) => new()
    {
        Form = form,
        Route = key is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [RouteKey] = key },
        Claims = request.Claims,
        Now = request.Now,
    };
}
