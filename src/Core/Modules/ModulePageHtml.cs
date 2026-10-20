using System.Text.RegularExpressions;
using Ganss.Xss;

namespace Rakit.Core.Modules;

/// <summary>
/// HTML of a Page module. The builder's HTML is sanitized (scripts, iframes, event handlers and other dangerous markup are
/// dropped) when it is saved and again when it is rendered. Another module is embedded with <c>{{module:GUID}}</c> on its
/// own (between tags or at the start / end of the text): after sanitizing, the server swaps such a marker for the
/// markup of its choice (an iframe in the runtime page). A marker anywhere else, for instance inside an attribute, stays text.
/// </summary>
public static partial class ModulePageHtml
{
    /// <summary>Most embedded modules one page may hold.</summary>
    public const int MaxEmbeds = 10;

    private const string Guid36 = @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    [GeneratedRegex(@"\{\{module:(" + Guid36 + @")\}\}")]
    private static partial Regex AnyMarker();

    // standalone: after a tag end, the start or another marker, and before a tag start, the end or another marker
    // (whitespace around is fine), so markers written one after the other each count
    [GeneratedRegex(@"(?<=^|>|\{\{module:" + Guid36 + @"\}\}\s*)\s*\{\{module:(" + Guid36 + @")\}\}\s*(?=<|$|\{\{module:)")]
    private static partial Regex StandaloneMarker();

    /// <summary>
    /// The library defaults (no script, iframe, object, event handlers or javascript: links) with three changes: Bootstrap
    /// classes are kept (the page is styled with them), form controls are dropped (a page cannot post anywhere), and the CSS
    /// properties that position content over the rest of the page are dropped.
    /// </summary>
    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        foreach (var tag in new[] { "form", "input", "button", "textarea", "select", "option", "optgroup", "fieldset", "datalist" })
            sanitizer.AllowedTags.Remove(tag);
        sanitizer.AllowedAttributes.Add("class");
        foreach (var property in new[] { "position", "top", "right", "bottom", "left", "z-index" })
            sanitizer.AllowedCssProperties.Remove(property);
        return sanitizer;
    }

    public static string Sanitize(string? html) => CreateSanitizer().Sanitize(html ?? "");

    /// <summary>Every module marker in the text (standalone or not), distinct, in order of appearance.</summary>
    public static IReadOnlyList<Guid> EmbeddedModuleIds(string? html)
        => AnyMarker().Matches(html ?? "").Select(m => Guid.Parse(m.Groups[1].Value)).Distinct().ToList();

    /// <summary>
    /// The sanitized HTML with each standalone marker replaced by <paramref name="markupFor"/>(module id), or removed when
    /// <paramref name="drop"/> (a page that is itself embedded does not embed further).
    /// </summary>
    public static string Render(string? html, Func<Guid, string> markupFor, bool drop = false)
        => StandaloneMarker().Replace(Sanitize(html), m => drop ? "" : markupFor(Guid.Parse(m.Groups[1].Value)));
}
