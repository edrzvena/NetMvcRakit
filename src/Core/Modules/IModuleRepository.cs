namespace Rakit.Core.Modules;

public interface IModuleRepository
{
    /// <summary>Ordered by group name (ungrouped first), then module name. <paramref name="status"/> null = all.</summary>
    Task<IReadOnlyList<ModuleSummary>> ListAsync(ModuleStatus? status = null, CancellationToken ct = default);

    Task<Module?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Creates a module in status Draft. Validates the input first.</summary>
    /// <exception cref="ModuleValidationException">Invalid input, or the group does not exist.</exception>
    Task<Module> CreateAsync(ModuleInput input, string user, CancellationToken ct = default);

    /// <summary>Saves new content; the status stays as it is.</summary>
    /// <exception cref="ModuleValidationException"/>
    /// <exception cref="ModuleNotFoundException"/>
    Task<Module> UpdateAsync(Guid id, ModuleInput input, string user, CancellationToken ct = default);

    /// <summary>Publishing re-validates the stored configuration first.</summary>
    /// <exception cref="ModuleValidationException">Publishing a module whose stored configuration is no longer valid.</exception>
    /// <exception cref="ModuleNotFoundException"/>
    Task<Module> SetStatusAsync(Guid id, ModuleStatus status, string user, CancellationToken ct = default);

    /// <summary>Duplicates the configuration as a new Draft named "{name} (salinan)".</summary>
    /// <exception cref="ModuleNotFoundException"/>
    Task<Module> CopyAsync(Guid id, string user, CancellationToken ct = default);

    /// <summary>Soft delete. Refused while a (non-deleted) menu item, a Page module that embeds it, or a workflow's Module step still points at the module.</summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleInUseException"/>
    Task DeleteAsync(Guid id, string user, CancellationToken ct = default);

    /// <summary>The (non-deleted) modules whose configuration mentions the data source anywhere (list, record actions, lookups).</summary>
    Task<IReadOnlyList<Module>> ListUsingDataSourceAsync(Guid dataSourceId, CancellationToken ct = default);
}

public interface IModuleGroupRepository
{
    Task<IReadOnlyList<ModuleGroup>> ListAsync(CancellationToken ct = default);

    Task<ModuleGroup?> GetAsync(Guid id, CancellationToken ct = default);

    /// <exception cref="ModuleGroupValidationException"/>
    /// <exception cref="DuplicateModuleGroupNameException"/>
    Task<ModuleGroup> CreateAsync(string name, string user, CancellationToken ct = default);

    /// <exception cref="ModuleGroupValidationException"/>
    /// <exception cref="DuplicateModuleGroupNameException"/>
    /// <exception cref="ModuleGroupNotFoundException"/>
    Task<ModuleGroup> UpdateAsync(Guid id, string name, bool isActive, string user, CancellationToken ct = default);

    /// <summary>Soft delete. Refused while (non-deleted) modules still belong to the group.</summary>
    /// <exception cref="ModuleGroupNotFoundException"/>
    /// <exception cref="ModuleGroupInUseException"/>
    Task DeleteAsync(Guid id, string user, CancellationToken ct = default);
}

public interface IMenuRepository
{
    /// <summary>Every non-deleted item (flat), ordered by Order then Label. The tree is built by the caller.</summary>
    Task<IReadOnlyList<Menu>> ListAsync(CancellationToken ct = default);

    Task<Menu?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// At most two levels: a parent must be a root, non-delimiter item. The module (if any) must exist.
    /// </summary>
    /// <exception cref="MenuValidationException"/>
    Task<Menu> CreateAsync(MenuInput input, string user, CancellationToken ct = default);

    /// <summary>An item that has children cannot itself become a child.</summary>
    /// <exception cref="MenuValidationException"/>
    /// <exception cref="MenuNotFoundException"/>
    Task<Menu> UpdateAsync(Guid id, MenuInput input, string user, CancellationToken ct = default);

    /// <summary>Soft delete. Refused while child items still hang under it.</summary>
    /// <exception cref="MenuNotFoundException"/>
    /// <exception cref="MenuHasChildrenException"/>
    Task DeleteAsync(Guid id, string user, CancellationToken ct = default);
}
