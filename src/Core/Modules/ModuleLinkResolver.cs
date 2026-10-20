using Rakit.Core.Workflows;

namespace Rakit.Core.Modules;

/// <summary>Lets a workflow's Module step link to a published module without depending on the module repository itself.</summary>
public sealed class ModuleLinkResolver(IModuleRepository modules) : IModuleLinkResolver
{
    public async Task<string?> PathOfAsync(Guid moduleId, CancellationToken ct = default)
    {
        var module = await modules.GetAsync(moduleId, ct);
        return module is { Status: ModuleStatus.Published } ? $"/object/page/{module.Id}" : null;
    }
}
