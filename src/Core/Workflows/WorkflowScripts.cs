using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Runtime;
using Microsoft.Extensions.Options;

namespace Rakit.Core.Workflows;

public enum ScriptFailureKind
{
    None,

    /// <summary>The script threw <c>new Error("text")</c> (or a plain value) on purpose: the text is for the user.</summary>
    Thrown,

    /// <summary>A bug in the script (TypeError, ReferenceError, ...) or a syntax error: generic message, details for the builder and the log.</summary>
    Error,

    /// <summary>Time, memory, statement, recursion or result size limit.</summary>
    Limit,
}

/// <param name="Json">The returned value as JSON text, or null when the script returned nothing (undefined / null).</param>
/// <param name="Message">Safe to show to end users when the run failed.</param>
/// <param name="Detail">The real reason, for the builder's "Uji skrip" and the log only.</param>
public sealed record ScriptResult(ScriptFailureKind Failure, string? Json, string? Message, string? Detail, int DurationMs)
{
    public bool Ok => Failure == ScriptFailureKind.None;
}

public interface IWorkflowScriptEngine
{
    /// <summary>
    /// Runs the body of <c>function (ctx) { ... }</c> in a sandbox: no .NET, file or network access, strict mode, with the time,
    /// memory, statement and recursion limits of <c>Rakit:Workflow:Script</c>. <paramref name="contextJson"/> is the JSON of
    /// <c>ctx</c>. Never throws for a failing script; the result says what happened.
    /// </summary>
    ScriptResult Run(string source, string contextJson);

    /// <summary>Null when the source parses, otherwise the parser's message (with the line).</summary>
    string? CheckSyntax(string source);
}

public sealed class JintScriptEngine(IOptions<WorkflowOptions> options) : IWorkflowScriptEngine
{
    public const string GenericError = "Skrip gagal dijalankan.";
    public const string LimitMessage = "Skrip melebihi batas yang diizinkan (waktu, memori, atau ukuran hasil).";

    // The source follows on the same line as the header so a reported line number is the line in the editor.
    private static string Wrap(string source) => "function __run(ctx){" + source + "\n}";

    public ScriptResult Run(string source, string contextJson)
    {
        var limits = options.Value.Script;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int Elapsed() => (int)stopwatch.ElapsedMilliseconds;

        try
        {
            var engine = new Engine(o =>
            {
                o.Strict();
                o.TimeoutInterval(TimeSpan.FromMilliseconds(Math.Clamp(limits.TimeoutMs, 10, ScriptOptions.MaxTimeoutMs)));
                o.LimitMemory(Math.Clamp(limits.MemoryLimitMb, 1, ScriptOptions.MaxMemoryMb) * 1024L * 1024L);
                o.MaxStatements(Math.Max(1_000, limits.MaxStatements));
                o.LimitRecursion(Math.Clamp(limits.MaxRecursion, 5, 1_000));
            });

            engine.SetValue("__ctxJson", contextJson);
            engine.Execute("globalThis.eval = undefined;");
            engine.Execute(Wrap(source));
            var returned = engine.Evaluate(
                "(function(){ var r = __run(JSON.parse(__ctxJson)); return r === undefined || r === null ? null : JSON.stringify(r); })()");

            if (returned.IsNull() || returned.IsUndefined()) return new ScriptResult(ScriptFailureKind.None, null, null, null, Elapsed());
            var json = returned.AsString();
            if (json is null) return new ScriptResult(ScriptFailureKind.None, null, null, null, Elapsed());
            if (Encoding.UTF8.GetByteCount(json) > limits.MaxResultBytes)
                return new ScriptResult(ScriptFailureKind.Limit, null, LimitMessage, $"Hasil lebih dari {limits.MaxResultBytes} byte.", Elapsed());
            return new ScriptResult(ScriptFailureKind.None, json, null, null, Elapsed());
        }
        catch (JavaScriptException ex)
        {
            var thrown = Describe(ex);
            return thrown.OnPurpose
                ? new ScriptResult(ScriptFailureKind.Thrown, null, thrown.Text, thrown.Text, Elapsed())
                : new ScriptResult(ScriptFailureKind.Error, null, GenericError, thrown.Text, Elapsed());
        }
        catch (Exception ex) when (ex is TimeoutException or MemoryLimitExceededException or StatementsCountOverflowException
            or RecursionDepthOverflowException or ExecutionCanceledException)
        {
            return new ScriptResult(ScriptFailureKind.Limit, null, LimitMessage, LimitDetail(ex), Elapsed());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // a syntax error, a value that cannot be read as JSON, ...
            return new ScriptResult(ScriptFailureKind.Error, null, GenericError, Shorten(ex.Message), Elapsed());
        }
    }

    public string? CheckSyntax(string source)
    {
        try
        {
            _ = Engine.PrepareScript(Wrap(source));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Shorten(ex.Message);
        }
    }

    private static (bool OnPurpose, string Text) Describe(JavaScriptException ex)
    {
        var error = ex.Error;
        if (error.IsObject())
        {
            var obj = error.AsObject();
            var name = obj.Get("name").ToString();
            var message = obj.Get("message").ToString();
            if (name == "Error") return (true, Shorten(message));
            var where = ex.Location.Start.Line > 0 ? $" (baris {ex.Location.Start.Line})" : "";
            return (false, Shorten($"{name}: {message}{where}"));
        }
        // throw "text" / throw 42
        return (true, Shorten(error.ToString()));
    }

    private static string LimitDetail(Exception ex) => ex switch
    {
        TimeoutException => "Melebihi batas waktu.",
        MemoryLimitExceededException => "Melebihi batas memori.",
        StatementsCountOverflowException => "Melebihi batas jumlah pernyataan.",
        RecursionDepthOverflowException => "Rekursi terlalu dalam.",
        _ => "Dibatalkan.",
    };

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 300 ? line : line[..300];
    }
}

/// <summary>Builds the <c>ctx</c> a script receives from the values of a run.</summary>
public static class WorkflowScriptContext
{
    /// <summary><c>{ form, key, claims, now, steps }</c>; <c>steps.code.row.Column</c> mirrors the output key <c>code.row.Column</c>.</summary>
    public static string ToJson(WorkflowValues values)
    {
        var steps = new JsonObject();
        foreach (var (key, value) in values.Outputs)
        {
            var parts = key.Split('.', 3);
            if (parts.Length < 2) continue;
            var node = Child(steps, parts[0]);
            if (parts.Length == 2) node[parts[1]] = ToNode(value);
            else Child(node, parts[1])[parts[2]] = ToNode(value);
        }

        // a step with a JSON body (Rest, Javascript) also offers it parsed as steps.code.json
        foreach (var (name, node) in steps.ToList())
        {
            if (node is JsonObject obj && obj["body"] is JsonValue body && body.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                try { obj["json"] = JsonNode.Parse(text); }
                catch (JsonException) { /* not JSON: only the text is offered */ }
            }
        }

        var form = new JsonObject();
        foreach (var (k, v) in values.Form) form[k] = v;
        var claims = new JsonObject();
        foreach (var (k, v) in values.Claims) claims[k] = v;

        var ctx = new JsonObject
        {
            ["form"] = form,
            ["key"] = values.Key,
            ["claims"] = claims,
            ["now"] = values.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["steps"] = steps,
        };
        return ctx.ToJsonString();
    }

    private static JsonObject Child(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null or DBNull => null,
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        decimal d => JsonValue.Create(d),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        float f when float.IsFinite(f) => JsonValue.Create((double)f),
        DateTime d => JsonValue.Create(d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        DateTimeOffset d => JsonValue.Create(d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(WorkflowValues.ToText(value)),
    };
}
