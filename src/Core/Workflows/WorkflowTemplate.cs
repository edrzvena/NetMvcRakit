using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rakit.Core.DataSources;

namespace Rakit.Core.Workflows;

/// <summary>
/// <c>{{key}}</c> placeholders in step texts (URLs, headers, bodies, email, pages, conditions). Keys:
/// <c>form.Name</c> (a value of the button's form), <c>key</c> (the record key of a per-row button), <c>claim.name</c>,
/// <c>now</c>, and the outputs of an earlier step: <c>code.return</c>, <c>code.out.Parameter</c>, <c>code.row.Column</c>
/// (data source steps), <c>code.status</c>, <c>code.body</c>, <c>code.json.path</c> (Rest), <c>code.sent</c> (Email).
/// An unknown key becomes empty text. The caller chooses how a value is encoded for where it lands (URL, JSON, HTML).
/// </summary>
public static partial class WorkflowTemplate
{
    /// <summary>First part of the keys that are not a step's output.</summary>
    public static readonly IReadOnlyList<string> Roots = ["form", "key", "claim", "now"];

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.\-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    public static bool HasPlaceholders(string? text) => !string.IsNullOrEmpty(text) && Placeholder().IsMatch(text);

    /// <summary>Every key used in <paramref name="text"/>, in order, without repeats.</summary>
    public static IReadOnlyList<string> Keys(string? text)
        => string.IsNullOrEmpty(text) ? [] : Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The first part of a key (<c>create</c> in <c>create.row.Id</c>).</summary>
    public static string RootOf(string key)
    {
        var cut = key.IndexOf('.');
        return cut < 0 ? key : key[..cut];
    }

    /// <summary>Replaces each placeholder with <paramref name="encode"/>(value); null encode = the value as it is.</summary>
    public static string Expand(string? text, Func<string, string?> lookup, Func<string, string>? encode = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Placeholder().Replace(text, m =>
        {
            var value = lookup(m.Groups[1].Value) ?? "";
            return encode is null ? value : encode(value);
        });
    }

    /// <summary>Escapes a value so it can sit inside a JSON string.</summary>
    public static string JsonEscape(string value)
    {
        var encoded = JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
        return encoded;
    }
}

/// <summary>
/// Everything a run can put in a placeholder: the button's form values, the record key, claims, the clock and the outputs of
/// the steps that already ran. The same values feed the data sources' StepOutput / FormField / RouteValue mappings.
/// </summary>
public sealed class WorkflowValues(
    IReadOnlyDictionary<string, string?> form, string? key, IReadOnlyDictionary<string, string?> claims, DateTimeOffset now)
{
    /// <summary>Output values by full key (<c>create.out.NewId</c>), case-insensitive.</summary>
    public Dictionary<string, object?> Outputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Key { get; } = key;

    /// <summary>The text for <paramref name="placeholderKey"/>, or null when there is none.</summary>
    public string? Get(string placeholderKey)
    {
        if (placeholderKey.Equals("key", StringComparison.OrdinalIgnoreCase)) return Key;
        if (placeholderKey.Equals("now", StringComparison.OrdinalIgnoreCase)) return now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (placeholderKey.StartsWith("form.", StringComparison.OrdinalIgnoreCase)) return Find(form, placeholderKey[5..]);
        if (placeholderKey.StartsWith("claim.", StringComparison.OrdinalIgnoreCase)) return Find(claims, placeholderKey[6..]);
        if (Outputs.TryGetValue(placeholderKey, out var output)) return ToText(output);

        var cut = placeholderKey.IndexOf(".json.", StringComparison.OrdinalIgnoreCase);
        if (cut > 0 && Outputs.TryGetValue(placeholderKey[..cut] + ".body", out var body) && body is string json)
            return JsonPath(json, placeholderKey[(cut + 6)..]);
        return null;
    }

    /// <summary>A fresh context for a data source: the form, the key as RouteValue <c>id</c>, claims, the clock and the outputs so far.</summary>
    public ParameterContext ToParameterContext() => new()
    {
        Form = new Dictionary<string, string?>(form, StringComparer.OrdinalIgnoreCase),
        Route = Key is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["id"] = Key },
        Claims = claims,
        Now = now,
        StepOutputs = new Dictionary<string, object?>(Outputs, StringComparer.OrdinalIgnoreCase),
    };

    private static string? Find(IReadOnlyDictionary<string, string?> source, string name)
    {
        if (source.TryGetValue(name, out var exact)) return exact;
        foreach (var (k, v) in source)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    public static string? ToText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>A dotted path into a JSON text: property names and array indexes (<c>items.0.id</c>). Null when it does not lead anywhere.</summary>
    private static string? JsonPath(string json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var node = document.RootElement;
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.ValueKind == JsonValueKind.Object)
                {
                    JsonElement found = default;
                    var ok = node.TryGetProperty(part, out found);
                    if (!ok)
                        foreach (var property in node.EnumerateObject())
                            if (string.Equals(property.Name, part, StringComparison.OrdinalIgnoreCase)) { found = property.Value; ok = true; break; }
                    if (!ok) return null;
                    node = found;
                }
                else if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < node.GetArrayLength())
                {
                    node = node[i];
                }
                else
                {
                    return null;
                }
            }

            return node.ValueKind switch
            {
                JsonValueKind.String => node.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => node.GetRawText() is { Length: > 2000 } raw ? raw[..2000] : node.GetRawText(),
            };
        }
        catch (JsonException)
        {
            return null; // the body is not JSON
        }
    }
}

/// <summary>Evaluates a Condition step.</summary>
public static class WorkflowCondition
{
    public static bool Evaluate(ConditionStepConfig condition, Func<string, string?> lookup)
    {
        var left = (WorkflowTemplate.Expand(condition.Left, lookup) ?? "").Trim();
        var right = (WorkflowTemplate.Expand(condition.Right, lookup) ?? "").Trim();

        switch (condition.Operator)
        {
            case ConditionOperator.Empty: return left.Length == 0;
            case ConditionOperator.NotEmpty: return left.Length > 0;
            case ConditionOperator.Contains: return left.Contains(right, StringComparison.OrdinalIgnoreCase);
        }

        var leftIsNumber = decimal.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a);
        var rightIsNumber = decimal.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b);
        var numeric = leftIsNumber && rightIsNumber;
        var order = numeric ? a.CompareTo(b) : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return condition.Operator switch
        {
            ConditionOperator.Eq => order == 0,
            ConditionOperator.Ne => order != 0,
            ConditionOperator.Gt => order > 0,
            ConditionOperator.Ge => order >= 0,
            ConditionOperator.Lt => order < 0,
            ConditionOperator.Le => order <= 0,
            _ => false,
        };
    }
}
