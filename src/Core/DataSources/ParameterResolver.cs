namespace Rakit.Core.DataSources;

/// <summary>A mapping is misconfigured (missing key, unknown expression). Safe to show to the builder.</summary>
public sealed class ParameterResolutionException(string parameterName, string message) : Exception(message)
{
    public string ParameterName { get; } = parameterName;
}

public interface IParameterResolver
{
    /// <summary>
    /// Evaluates every mapping against <paramref name="context"/> and returns name → value, ready to pass to
    /// <c>ISqlExecutionService</c>. A source that has no value yields the mapping's <c>Value</c> as default,
    /// or null when that is empty too.
    /// </summary>
    /// <exception cref="ParameterResolutionException"/>
    IDictionary<string, object?> Resolve(IReadOnlyList<ParameterMapping> mappings, ParameterContext context);
}

public sealed class ParameterResolver : IParameterResolver
{
    /// <summary>Expressions DS-05 allows (case-insensitive).</summary>
    public static readonly IReadOnlyList<string> KnownExpressions = ["@now", "@utcnow", "@today"];

    public static bool IsKnownExpression(string? expression)
        => expression is not null && KnownExpressions.Contains(expression.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool RequiresKey(ParameterSource source)
        => source is ParameterSource.QueryString or ParameterSource.FormField or ParameterSource.RouteValue
            or ParameterSource.UserClaim or ParameterSource.StepOutput;

    public IDictionary<string, object?> Resolve(IReadOnlyList<ParameterMapping> mappings, ParameterContext context)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(context);

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
            result[mapping.Name] = ResolveOne(mapping, context);
        return result;
    }

    private static object? ResolveOne(ParameterMapping m, ParameterContext ctx)
    {
        if (RequiresKey(m.Source) && string.IsNullOrWhiteSpace(m.Key))
            throw new ParameterResolutionException(m.Name, $"Parameter '{m.Name}': sumber {m.Source} membutuhkan Key.");

        return m.Source switch
        {
            ParameterSource.QueryString => Lookup(ctx.QueryString, m.Key!) ?? m.Value,
            ParameterSource.FormField => Lookup(ctx.Form, m.Key!) ?? m.Value,
            ParameterSource.RouteValue => Lookup(ctx.Route, m.Key!) ?? m.Value,
            ParameterSource.UserClaim => Lookup(ctx.Claims, m.Key!) ?? m.Value,
            ParameterSource.StepOutput => Lookup(ctx.StepOutputs, m.Key!) ?? m.Value,
            ParameterSource.Constant => m.Value,
            ParameterSource.Expression => Evaluate(m, ctx),
            _ => throw new ParameterResolutionException(m.Name, $"Parameter '{m.Name}': sumber {m.Source} tidak dikenal."),
        };
    }

    private static object Evaluate(ParameterMapping m, ParameterContext ctx)
    {
        var expression = m.Value?.Trim().ToLowerInvariant();
        return expression switch
        {
            "@now" => ctx.Now.DateTime,
            "@utcnow" => ctx.Now.UtcDateTime,
            "@today" => ctx.Now.Date,
            _ => throw new ParameterResolutionException(
                m.Name, $"Parameter '{m.Name}': ekspresi '{m.Value}' tidak dikenal. Yang didukung: {string.Join(", ", KnownExpressions)}."),
        };
    }

    private static T? Lookup<T>(IReadOnlyDictionary<string, T> source, string key)
    {
        if (source.TryGetValue(key, out var exact)) return exact;
        foreach (var (k, v) in source)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
        return default;
    }
}
