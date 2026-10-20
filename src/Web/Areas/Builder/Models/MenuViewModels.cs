using Rakit.Core.Modules;

namespace Rakit.Web.Areas.Builder.Models;

public enum MenuTargetType { Module, Url, Folder, Delimiter }

/// <param name="Depth">0 for a top-level item, 1 for a child.</param>
/// <param name="Target">What the item points at, in words.</param>
/// <param name="Hidden">Why the item is not shown to visitors right now (inactive, module not published, ...); null = shown.</param>
public sealed record MenuRow(Menu Item, int Depth, string Target, string? Hidden);

public sealed record MenuIndexViewModel(IReadOnlyList<MenuRow> Rows, string? Message, string? Error);

public sealed record MenuModuleOption(Guid Id, string Label);

public sealed record MenuParentOption(Guid Id, string Label);

/// <summary>Posted fields: Id, Label, Icon, TargetType, ModuleId, Url, ParentId, IsActive, Order.</summary>
public sealed class MenuFormViewModel
{
    public Guid? Id { get; set; }
    public string Label { get; set; } = "";
    public string? Icon { get; set; }
    public MenuTargetType TargetType { get; set; } = MenuTargetType.Module;
    public Guid? ModuleId { get; set; }
    public string? Url { get; set; }
    public Guid? ParentId { get; set; }
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }

    // Display only.
    public IReadOnlyList<MenuModuleOption> Modules { get; set; } = [];
    public IReadOnlyList<MenuParentOption> Parents { get; set; } = [];

    /// <summary>The item has children, so it can neither get a parent nor change into a delimiter.</summary>
    public bool HasChildren { get; set; }

    public bool IsEdit => Id is not null;

    public static MenuTargetType TargetOf(Menu menu) =>
        menu.IsDelimiter ? MenuTargetType.Delimiter
        : menu.ModuleId is not null ? MenuTargetType.Module
        : menu.Url is not null ? MenuTargetType.Url
        : MenuTargetType.Folder;
}
