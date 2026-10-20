using Rakit.Core.Modules;

namespace Rakit.Web.Models;

/// <summary>What the home page shows: counts of what exists and the pages that are published.</summary>
public sealed record HomeViewModel(
    int ModulesPublished, int ModulesTotal, int DataSources, int Workflows, int MenuItems,
    IReadOnlyList<ModuleSummary> Published, bool DatabaseAvailable);
