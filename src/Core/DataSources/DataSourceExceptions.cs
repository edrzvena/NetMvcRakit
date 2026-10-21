namespace Rakit.Core.DataSources;

public sealed class DataSourceNotFoundException(Guid id) : Exception($"Data source '{id}' not found.");

public sealed class DuplicateDataSourceCodeException(string code) : Exception($"Data source code '{code}' is already used.");

/// <summary>Modules (any status) or workflows still point at the data source, so deleting it would break them when they run.</summary>
public sealed class DataSourceInUseException(Guid id, int moduleCount, int workflowCount)
    : Exception($"Data source '{id}' is still used by {moduleCount} module(s) and {workflowCount} workflow(s).")
{
    public int ModuleCount { get; } = moduleCount;
    public int WorkflowCount { get; } = workflowCount;
}

/// <summary>The input failed validation; <see cref="Errors"/> are safe to show to the builder.</summary>
public sealed class DataSourceValidationException(IReadOnlyList<string> errors)
    : Exception("Data source input is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
