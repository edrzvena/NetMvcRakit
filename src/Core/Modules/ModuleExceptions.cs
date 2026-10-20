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

/// <summary>The record has no row in the Detail data source (deleted meanwhile, or a wrong key).</summary>
public sealed class ModuleRecordNotFoundException(Guid moduleId) : Exception($"Record of module '{moduleId}' not found.");

/// <summary>
/// The submitted values or key were rejected. <see cref="Errors"/> maps a form field name to its message; the key ""
/// holds a message about the request as a whole. All messages are safe to show to end users.
/// </summary>
public sealed class ModuleRecordValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception("Record input is invalid: " + string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value}")))
{
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}

/// <summary>The procedure ran but returned a non-zero code; the transaction was rolled back. The message is safe to show.</summary>
public sealed class ModuleRecordRejectedException(int code) : Exception($"Operasi ditolak (kode {code}).")
{
    public int Code { get; } = code;
}

/// <summary>
/// The action cannot run because of how the module is set up (not configured, data source gone, switched off by a flag
/// or the whitelist, misconfigured mapping). The message is generic; the reason is logged.
/// </summary>
public sealed class ModuleRecordUnavailableException(string message, string reason, bool notConfigured, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Why, in plain words. For builder screens and logs only; never show it to end users.</summary>
    public string Reason { get; } = reason;

    /// <summary>The module simply has no data source for this action (the button should not exist).</summary>
    public bool NotConfigured { get; } = notConfigured;
}
