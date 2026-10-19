namespace Rakit.Core.DataSources;

public sealed class DataSourceNotFoundException(Guid id) : Exception($"Data source '{id}' not found.");

public sealed class DuplicateDataSourceCodeException(string code) : Exception($"Data source code '{code}' is already used.");

/// <summary>The input failed validation; <see cref="Errors"/> are safe to show to the builder.</summary>
public sealed class DataSourceValidationException(IReadOnlyList<string> errors)
    : Exception("Data source input is invalid: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
