using Rakit.Core.Data;

namespace Rakit.Core.Modules;

/// <summary>
/// One entry of the runtime navigation. A <see cref="Children"/> list that is not empty makes it a dropdown (its own
/// target, if it has one, is then not used). Module targets carry the module id; the page builds the address.
/// </summary>
public sealed record NavigationItem(
    Guid Id, string Label, string? Icon, Guid? ModuleId, string? Url, bool IsDelimiter, IReadOnlyList<NavigationItem> Children);

/// <summary>A menu row as the navigation tree builder needs it (already filtered and ordered).</summary>
public sealed record NavigationRow(
    Guid Id, string Label, string? Icon, Guid? ModuleId, string? Url, Guid? ParentId, bool IsDelimiter);

public interface IMenuNavigationService
{
    /// <summary>
    /// The menu a visitor sees: active items only, module items only while their module is Published, a child only
    /// while its parent is shown, a folder only while it has something to show, and no leading, trailing or doubled
    /// separators. Ordered by Order then Label.
    /// </summary>
    Task<IReadOnlyList<NavigationItem>> GetAsync(CancellationToken ct = default);
}

public sealed class MenuNavigationService(IDbConnectionFactory connections) : IMenuNavigationService
{
    public async Task<IReadOnlyList<NavigationItem>> GetAsync(CancellationToken ct = default)
    {
        await using var conn = await connections.CreateMetadataAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT m.Id, m.Label, m.Icon, m.ModuleId, m.Url, m.ParentId, m.IsDelimiter
FROM rakit.Menus m
LEFT JOIN rakit.Modules md ON md.Id = m.ModuleId AND md.IsDeleted = 0
WHERE m.IsDeleted = 0 AND m.IsActived = 1
  AND (m.ModuleId IS NULL OR md.Status = N'published')
ORDER BY m.[Order], m.Label";

        var rows = new List<NavigationRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new NavigationRow(
                reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5),
                reader.GetBoolean(6)));
        }
        return MenuNavigationTree.Build(rows);
    }
}

/// <summary>Builds the two-level navigation from flat, already ordered rows. Pure: no database, no routing.</summary>
public static class MenuNavigationTree
{
    public static IReadOnlyList<NavigationItem> Build(IReadOnlyList<NavigationRow> rows)
    {
        var byParent = rows.Where(r => r.ParentId is not null).ToLookup(r => r.ParentId!.Value);
        var roots = new List<NavigationItem>();

        foreach (var root in rows.Where(r => r.ParentId is null))
        {
            // Children of a root that is not in the list (inactive, deleted, module not published) are simply never reached.
            var children = root.IsDelimiter
                ? []
                : TrimSeparators(byParent[root.Id].Select(c => new NavigationItem(c.Id, c.Label, c.Icon, c.ModuleId, c.Url, c.IsDelimiter, [])).ToList());

            var isFolder = !root.IsDelimiter && root.ModuleId is null && root.Url is null;
            if (isFolder && children.Count == 0) continue; // a title with nothing under it

            roots.Add(new NavigationItem(root.Id, root.Label, root.Icon, root.ModuleId, root.Url, root.IsDelimiter, children));
        }

        return TrimSeparators(roots);
    }

    /// <summary>Drops separators at the start, at the end and right after another separator.</summary>
    private static List<NavigationItem> TrimSeparators(List<NavigationItem> items)
    {
        var result = new List<NavigationItem>();
        foreach (var item in items)
        {
            if (item.IsDelimiter && (result.Count == 0 || result[^1].IsDelimiter)) continue;
            result.Add(item);
        }
        while (result.Count > 0 && result[^1].IsDelimiter) result.RemoveAt(result.Count - 1);
        return result;
    }
}
