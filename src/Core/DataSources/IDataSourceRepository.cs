namespace Rakit.Core.DataSources;

public interface IDataSourceRepository
{
    Task<IReadOnlyList<DataSourceSummary>> ListAsync(CancellationToken ct = default);

    Task<DataSource?> GetAsync(Guid id, CancellationToken ct = default);

    Task<DataSource?> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Creates version 1. Validates the input first.</summary>
    /// <exception cref="DataSourceValidationException"/>
    /// <exception cref="DuplicateDataSourceCodeException"/>
    Task<DataSource> CreateAsync(DataSourceInput input, string user, CancellationToken ct = default);

    /// <summary>Saves the change as a new version (previous versions stay in history).</summary>
    /// <exception cref="DataSourceValidationException"/>
    /// <exception cref="DuplicateDataSourceCodeException"/>
    /// <exception cref="DataSourceNotFoundException"/>
    Task<DataSource> UpdateAsync(Guid id, DataSourceInput input, string user, CancellationToken ct = default);

    /// <summary>Soft delete. <exception cref="DataSourceNotFoundException"/></summary>
    Task DeleteAsync(Guid id, string user, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<DataSourceVersion>> GetVersionsAsync(Guid id, CancellationToken ct = default);
}
