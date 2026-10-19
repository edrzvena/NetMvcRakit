using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Sql;

namespace Rakit.Core.DataSources;

/// <summary>A global switch (AllowRawQuery / AllowWrite) forbids this data source from running. Safe to show to the builder.</summary>
public sealed class DataSourceForbiddenException(string message) : Exception(message);

/// <param name="Skip">Rows to skip (paging).</param>
/// <param name="Take">Page size (paging).</param>
/// <param name="OrderBy">Optional <c>column_asc</c> / <c>column_desc</c>.</param>
/// <param name="Search">Optional free-text search.</param>
/// <param name="IncludeTotal">Also compute the total row count (table mode, or queries that use @IsCount).</param>
public sealed record DataSourceRunRequest(
    int Skip = 0, int Take = 10, string? OrderBy = null, string? Search = null, bool IncludeTotal = true);

/// <param name="TotalCount">Total rows ignoring paging; null when it is unknown (query without @IsCount) or not requested.</param>
public sealed record DataSourceResult(
    IReadOnlyList<DataTableResult> ResultSets, int? TotalCount, int RowsAffected, TimeSpan Duration);

public interface IDataSourceService
{
    /// <summary>Runs a saved data source, resolving its parameter mappings from <paramref name="context"/>.</summary>
    /// <exception cref="DataSourceNotFoundException"/>
    /// <exception cref="DataSourceForbiddenException"/>
    /// <exception cref="ParameterResolutionException"/>
    /// <exception cref="TableDefinitionException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<DataSourceResult> ExecuteAsync(
        Guid dataSourceId, ParameterContext context, DataSourceRunRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// Builder preview of an unsaved definition: at most 50 rows, statement type Query only. Parameters take their
    /// values from <paramref name="sampleValues"/> by name (missing = null); no resolver is involved.
    /// </summary>
    /// <exception cref="DataSourceValidationException"/>
    /// <exception cref="DataSourceForbiddenException"/>
    /// <exception cref="TableDefinitionException"/>
    /// <exception cref="SqlExecutionException"/>
    Task<DataSourceResult> PreviewAsync(
        DataSourceInput input, IDictionary<string, object?> sampleValues, CancellationToken ct = default);
}

public sealed class DataSourceService(
    IDataSourceRepository repository,
    IParameterResolver resolver,
    ITableQueryBuilder tableBuilder,
    ISqlExecutionService execution,
    IOptions<RakitDbOptions> options) : IDataSourceService
{
    public const int PreviewMaxRows = 50;

    public async Task<DataSourceResult> ExecuteAsync(
        Guid dataSourceId, ParameterContext context, DataSourceRunRequest? request = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ds = await repository.GetAsync(dataSourceId, ct) ?? throw new DataSourceNotFoundException(dataSourceId);

        EnsureAllowed(ds.Mode, ds.StatementType);
        var args = resolver.Resolve(ds.Definition.Parameters, context);
        return await RunAsync(ds.Mode, ds.StatementType, ds.Definition, args, request ?? new DataSourceRunRequest(), maxRows: null, ct);
    }

    public Task<DataSourceResult> PreviewAsync(
        DataSourceInput input, IDictionary<string, object?> sampleValues, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sampleValues);

        // Code/Name are irrelevant for a preview; fill them so only real problems are reported.
        var errors = DataSourceInputValidator.Validate(new DataSourceInput
        {
            Code = string.IsNullOrWhiteSpace(input.Code) ? "preview" : input.Code,
            Name = string.IsNullOrWhiteSpace(input.Name) ? "preview" : input.Name,
            Mode = input.Mode,
            StatementType = input.StatementType,
            Definition = input.Definition,
        });
        if (errors.Count > 0) throw new DataSourceValidationException(errors);

        if (input.StatementType != StatementType.Query)
            throw new DataSourceForbiddenException("Preview hanya tersedia untuk statement Query.");
        EnsureAllowed(input.Mode, input.StatementType);

        var samples = new Dictionary<string, object?>(sampleValues, StringComparer.OrdinalIgnoreCase);
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in input.Definition.Parameters)
            args[p.Name] = samples.GetValueOrDefault(p.Name);

        return RunAsync(input.Mode, input.StatementType, input.Definition, args,
            new DataSourceRunRequest(0, PreviewMaxRows), PreviewMaxRows, ct);
    }

    private void EnsureAllowed(DataSourceMode mode, StatementType statement)
    {
        var flags = options.Value;
        if (mode == DataSourceMode.Query && !flags.AllowRawQuery)
            throw new DataSourceForbiddenException("Query mentah dinonaktifkan (Rakit:Db:AllowRawQuery = false).");
        if (statement == StatementType.Command && !flags.AllowWrite)
            throw new DataSourceForbiddenException("Statement Command dinonaktifkan (Rakit:Db:AllowWrite = false).");
        if (mode == DataSourceMode.StoredProcedure)
            throw new DataSourceForbiddenException("Mode Stored Procedure belum didukung.");
    }

    private async Task<DataSourceResult> RunAsync(
        DataSourceMode mode, StatementType statement, DataSourceDefinition def,
        IDictionary<string, object?> args, DataSourceRunRequest request, int? maxRows, CancellationToken ct)
    {
        var execOptions = new SqlExecOptions
        {
            TimeoutSeconds = def.TimeoutSeconds,
            StatementType = statement,
            MaxRows = maxRows ?? SqlExecOptions.DefaultMaxRows,
        };

        return mode == DataSourceMode.Table
            ? await RunTableAsync(def, args, request, execOptions, ct)
            : await RunQueryAsync(def, args, request, execOptions, ct);
    }

    private async Task<DataSourceResult> RunTableAsync(
        DataSourceDefinition def, IDictionary<string, object?> args, DataSourceRunRequest request,
        SqlExecOptions execOptions, CancellationToken ct)
    {
        var query = await tableBuilder.BuildAsync(
            def.Table!, new TableQueryRequest(request.Skip, request.Take, request.OrderBy, request.Search), ct);

        var all = new Dictionary<string, object?>(query.PagingArgs, StringComparer.OrdinalIgnoreCase);
        foreach (var name in query.FilterParameters)
            all[name] = args.TryGetValue(name, out var v) ? v : null; // unmapped filter parameter = no filter

        var data = await execution.ExecuteQueryAsync(query.DataSql, all, execOptions, ct);

        int? total = null;
        if (request.IncludeTotal)
        {
            var count = await execution.ExecuteQueryAsync(query.CountSql, all, execOptions, ct);
            total = FirstCellAsInt(count);
        }

        return new DataSourceResult(data.ResultSets, total, data.RowsAffected, data.Duration);
    }

    private async Task<DataSourceResult> RunQueryAsync(
        DataSourceDefinition def, IDictionary<string, object?> args, DataSourceRunRequest request,
        SqlExecOptions execOptions, CancellationToken ct)
    {
        var sql = def.Query!;

        // DS-07 conventions are available to raw queries as native tokens; a mapped parameter of the same name wins.
        var all = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        all.TryAdd("Skip", request.Skip);
        all.TryAdd("Take", request.Take);
        all.TryAdd("OrderBy", request.OrderBy ?? "");
        all.TryAdd("Search", request.Search?.Trim() ?? "");
        all.TryAdd("IsCount", false);

        var result = await execution.ExecuteQueryAsync(sql, all, execOptions, ct);

        int? total = null;
        var usesIsCount = SqlTemplate.ReferencedParameters(SqlTemplate.Rewrite(sql).Sql)
            .Contains("IsCount", StringComparer.OrdinalIgnoreCase);
        if (request.IncludeTotal && usesIsCount && execOptions.StatementType == StatementType.Query)
        {
            var countArgs = new Dictionary<string, object?>(all, StringComparer.OrdinalIgnoreCase) { ["IsCount"] = true };
            total = FirstCellAsInt(await execution.ExecuteQueryAsync(sql, countArgs, execOptions, ct));
        }

        return new DataSourceResult(result.ResultSets, total, result.RowsAffected, result.Duration);
    }

    private static int? FirstCellAsInt(SqlExecResult result)
        => result.ResultSets.Count > 0 && result.ResultSets[0].Rows.Count > 0 && result.ResultSets[0].Rows[0].Count > 0
            && result.ResultSets[0].Rows[0][0] is { } cell
            ? Convert.ToInt32(cell, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
