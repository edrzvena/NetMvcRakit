namespace Rakit.Core.Modules;

public sealed class ModuleNotFoundException(Guid id) : Exception($"Module '{id}' not found.");

/// <summary>The input failed validation; <see cref="Errors"/> are safe to show to the builder.</summary>
public sealed class ModuleValidationException(IReadOnlyList<string> errors)
    : Exception("Module input is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>An active menu still points at the module.</summary>
public sealed class ModuleInUseException(Guid id, int menuCount)
    : Exception($"Module '{id}' is still used by {menuCount} menu item(s).")
{
    public int MenuCount { get; } = menuCount;
}

public sealed class ModuleGroupNotFoundException(Guid id) : Exception($"Module group '{id}' not found.");

public sealed class DuplicateModuleGroupNameException(string name) : Exception($"Module group name '{name}' is already used.");

public sealed class ModuleGroupValidationException(IReadOnlyList<string> errors)
    : Exception("Module group input is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Modules still belong to the group.</summary>
public sealed class ModuleGroupInUseException(Guid id, int moduleCount)
    : Exception($"Module group '{id}' still has {moduleCount} module(s).")
{
    public int ModuleCount { get; } = moduleCount;
}

public sealed class MenuNotFoundException(Guid id) : Exception($"Menu '{id}' not found.");

public sealed class MenuValidationException(IReadOnlyList<string> errors)
    : Exception("Menu input is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Child menu items still hang under the menu.</summary>
public sealed class MenuHasChildrenException(Guid id, int childCount)
    : Exception($"Menu '{id}' still has {childCount} child item(s).")
{
    public int ChildCount { get; } = childCount;
}
