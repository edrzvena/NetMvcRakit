using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Procedures;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

namespace Rakit.Core.DataSources;

/// <summary>A global switch (AllowRawQuery / AllowWrite / AllowStoredProcedure) or the SP whitelist forbids this data source from running. Safe to show to the builder.</summary>
public sealed class DataSourceForbiddenException(string message) : Exception(message);

/// <param name="Skip">Rows to skip (paging).</param>
/// <param name="Take">Page size (paging).</param>
/// <param name="OrderBy">Optional <c>column_asc</c> / <c>column_desc</c>.</param>
/// <param name="Search">Optional free-text search.</param>
/// <param name="IncludeTotal">Also compute the total row count (table mode, queries that use @IsCount, stored procedures with a count result set or @IsCount).</param>
/// <param name="RequireQuery">The data source must be a Query statement, otherwise <see cref="DataSourceForbiddenException"/> (for callers that must never write, e.g. a lookup read on a GET).</param>
/// <param name="CommitOnlyIfReturnValueZero">Stored procedures only: the call runs in a transaction that is committed only when the procedure returns 0; any other return value is rolled back and reported in <see cref="DataSourceResult.ReturnValue"/>.</param>
/// <param name="Scope">Run on this shared connection and transaction (a workflow with one transaction); its owner commits or rolls back, so <paramref name="CommitOnlyIfReturnValueZero"/> does not apply.</param>
public sealed record DataSourceRunRequest(
    int Skip = 0, int Take = 10, string? OrderBy = null, string? Search = null, bool IncludeTotal = true,
    bool CommitOnlyIfReturnValueZero = false, bool RequireQuery = false, Sql.ISqlScope? Scope = null);

/// <param name="TotalCount">Total rows ignoring paging; null when it is unknown (query without @IsCount) or not requested.</param>
public sealed record DataSourceResult(
    IReadOnlyList<DataTableResult> ResultSets, int? TotalCount, int RowsAffected, TimeSpan Duration)
{
    /// <summary>Stored procedures only: OUTPUT parameter values by name (no @). Null when the call was cut short by the row cap.</summary>
    public IDictionary<string, object?> OutputParameters { get; init; } = new Dictionary<string, object?>();

    /// <summary>Stored procedures only: the return code (convention: 0 = success). Not thrown as an error; the caller decides. Null when cut short.</summary>
    public int? ReturnValue { get; init; }

    /// <summary>
    /// True when the data source reads <c>@Skip</c> (a Table data source always does), so the rows are already the requested page;
    /// false when it does not and returns the same rows whatever page is asked for; null when that was not looked at (a stored
    /// procedure run that did not ask for a total).
    /// </summary>
    public bool? Pages { get; init; }

    /// <summary>Index into <see cref="ResultSets"/> of the rows to list (always 0 except for stored procedures that say otherwise).</summary>
    public int ListResultSetIndex { get; init; }

    /// <summary>The rows to list; null when that result set was not read (the procedure returned fewer sets, or reading stopped at the row cap).</summary>
    public DataTableResult? List => ListResultSetIndex < ResultSets.Count ? ResultSets[ListResultSetIndex] : null;
}

public interface IDataSourceService
{
    /// <summary>Runs a saved data source, resolving its parameter mappings from <paramref name="context"/>.</summary>
    /// <exception cref="DataSourceNotFoundException"/>
    /// <exception cref="DataSourceForbiddenException"/>
    /// <exception cref="ParameterResolutionException"/>
    /// <exception cref="TableDefinitionException"/>
    /// <exception cref="SchemaAccessDeniedException">Stored procedure schema is not in AllowedSchemas.</exception>
    /// <exception cref="SchemaObjectNotFoundException">Stored procedure no longer exists.</exception>
    /// <exception cref="ArgumentException">A stored procedure parameter has no value or a value that does not fit.</exception>
    /// <exception cref="SqlExecutionException"/>
    Task<DataSourceResult> ExecuteAsync(
        Guid dataSourceId, ParameterContext context, DataSourceRunRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// Builder preview of a SAVED data source with the request's paging, sort and search: at most 50 rows per result
    /// set, statement type Query only, and a stored procedure runs inside a transaction that is rolled back.
    /// Same flag and whitelist checks as <see cref="ExecuteAsync"/>.
    /// </summary>
    /// <exception cref="DataSourceNotFoundException"/>
    /// <exception cref="DataSourceForbiddenException">Flag, whitelist, or a Command statement.</exception>
    /// <exception cref="ParameterResolutionException"/>
    /// <exception cref="TableDefinitionException"/>
    /// <exception cref="SchemaAccessDeniedException">Stored procedure schema is not in AllowedSchemas.</exception>
    /// <exception cref="SchemaObjectNotFoundException">Stored procedure no longer exists.</exception>
    /// <exception cref="ArgumentException">A stored procedure parameter has no value or a value that does not fit.</exception>
    /// <exception cref="SqlExecutionException"/>
    Task<DataSourceResult> ExecuteForPreviewAsync(
        Guid dataSourceId, ParameterContext context, DataSourceRunRequest? request = null, CancellationToken ct = default);

    /// <summary>
    /// Builder preview of an unsaved definition: at most 50 rows, statement type Query only. Parameters take their
    /// values from <paramref name="sampleValues"/> by name (missing = null); no resolver is involved.
    /// A stored procedure runs inside a transaction that is rolled back.
    /// </summary>
    /// <exception cref="DataSourceValidationException"/>
    /// <exception cref="DataSourceForbiddenException"/>
    /// <exception cref="TableDefinitionException"/>
    /// <exception cref="SchemaAccessDeniedException">Stored procedure schema is not in AllowedSchemas.</exception>
    /// <exception cref="SchemaObjectNotFoundException">No such stored procedure.</exception>
    /// <exception cref="ArgumentException">A stored procedure parameter has no value or a value that does not fit.</exception>
    /// <exception cref="SqlExecutionException"/>
    Task<DataSourceResult> PreviewAsync(
        DataSourceInput input, IDictionary<string, object?> sampleValues, CancellationToken ct = default);
}

public sealed class DataSourceService(
    IDataSourceRepository repository,
    IParameterResolver resolver,
    ITableQueryBuilder tableBuilder,
    ISqlExecutionService execution,
    ISpWhitelistService whitelist,
    ISchemaBrowserService schemaBrowser,
    IOptions<RakitDbOptions> options) : IDataSourceService
{
    public const int PreviewMaxRows = 50;

    /// <summary>DS-07 parameter names the service fills from the run request; they need no mapping.</summary>
    public static readonly IReadOnlyList<string> PagingParameterNames = ["Skip", "Take", "OrderBy", "Search", "IsCount"];

    public Task<DataSourceResult> ExecuteAsync(
        Guid dataSourceId, ParameterContext context, DataSourceRunRequest? request = null, CancellationToken ct = default)
        => RunSavedAsync(dataSourceId, context, request, preview: false, ct);

    public Task<DataSourceResult> ExecuteForPreviewAsync(
        Guid dataSourceId, ParameterContext context, DataSourceRunRequest? request = null, CancellationToken ct = default)
        => RunSavedAsync(dataSourceId, context, request, preview: true, ct);

    private async Task<DataSourceResult> RunSavedAsync(
        Guid dataSourceId, ParameterContext context, DataSourceRunRequest? request, bool preview, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ds = await repository.GetAsync(dataSourceId, ct) ?? throw new DataSourceNotFoundException(dataSourceId);

        if (preview && ds.StatementType != StatementType.Query)
            throw new DataSourceForbiddenException("Preview hanya tersedia untuk statement Query.");
        if (request is { RequireQuery: true } && ds.StatementType != StatementType.Query)
            throw new DataSourceForbiddenException($"Data source '{ds.Code}' bukan statement Query.");
        await EnsureAllowedAsync(ds.Mode, ds.StatementType, ds.Definition, ct);

        var args = resolver.Resolve(ds.Definition.Parameters, context);
        return await RunAsync(ds.Mode, ds.StatementType, ds.Definition, args, request ?? new DataSourceRunRequest(),
            maxRows: preview ? PreviewMaxRows : null, rollback: preview, ct);
    }

    public async Task<DataSourceResult> PreviewAsync(
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
        await EnsureAllowedAsync(input.Mode, input.StatementType, input.Definition, ct);

        var samples = new Dictionary<string, object?>(sampleValues, StringComparer.OrdinalIgnoreCase);
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in input.Definition.Parameters)
            args[p.Name] = samples.GetValueOrDefault(p.Name);

        // A preview runs the real procedure, so it always runs inside a transaction that is rolled back.
        return await RunAsync(input.Mode, input.StatementType, input.Definition, args,
            new DataSourceRunRequest(0, PreviewMaxRows), PreviewMaxRows, rollback: true, ct);
    }

    private async Task EnsureAllowedAsync(DataSourceMode mode, StatementType statement, DataSourceDefinition def, CancellationToken ct)
    {
        var flags = options.Value;
        if (mode == DataSourceMode.Query && !flags.AllowRawQuery)
            throw new DataSourceForbiddenException("Query mentah dinonaktifkan (Rakit:Db:AllowRawQuery = false).");
        if (mode == DataSourceMode.StoredProcedure && !flags.AllowStoredProcedure)
            throw new DataSourceForbiddenException("Stored procedure dinonaktifkan (Rakit:Db:AllowStoredProcedure = false).");
        if (statement == StatementType.Command && !flags.AllowWrite)
            throw new DataSourceForbiddenException("Statement Command dinonaktifkan (Rakit:Db:AllowWrite = false).");

        if (mode == DataSourceMode.StoredProcedure)
        {
            var proc = def.Procedure ?? throw new InvalidOperationException("Data source stored procedure tidak punya prosedur.");
            if (!await whitelist.IsAllowedAsync(proc.Schema, proc.Name, ct))
                throw new DataSourceForbiddenException(
                    $"Prosedur '{proc.Schema}.{proc.Name}' tidak ada di whitelist atau tidak aktif.");
        }
    }

    private async Task<DataSourceResult> RunAsync(
        DataSourceMode mode, StatementType statement, DataSourceDefinition def,
        IDictionary<string, object?> args, DataSourceRunRequest request, int? maxRows, bool rollback, CancellationToken ct)
    {
        var execOptions = new SqlExecOptions
        {
            TimeoutSeconds = def.TimeoutSeconds,
            StatementType = statement,
            MaxRows = maxRows ?? SqlExecOptions.DefaultMaxRows,
            RollbackAfter = rollback,
            CommitOnlyIfReturnValueZero = request.CommitOnlyIfReturnValueZero,
            Scope = request.Scope,
        };

        return mode switch
        {
            DataSourceMode.Table => await RunTableAsync(def, args, request, execOptions, ct),
            DataSourceMode.StoredProcedure => await RunProcedureAsync(def, statement, args, request, execOptions, ct),
            _ => await RunQueryAsync(def, args, request, execOptions, ct),
        };
    }

    private async Task<DataSourceResult> RunTableAsync(
        DataSourceDefinition def, IDictionary<string, object?> args, DataSourceRunRequest request,
        SqlExecOptions execOptions, CancellationToken ct)
    {
        var query = await tableBuilder.BuildAsync(
            def.Table!, new TableQueryRequest(request.Skip, request.Take, request.OrderBy, request.Search), ct);

        var all = new Dictionary<string, object?>(query.PagingArgs, StringComparer.OrdinalIgnoreCase);
        foreach (var name in query.FilterParameters)
        {
            var value = args.TryGetValue(name, out var v) ? v : null; // unmapped filter parameter = no filter

            // Text (a URL, a form) becomes the compared column's type here: "abc" for an int column is the visitor's
            // mistake (ArgumentException, a 400 for the caller) instead of a conversion error in the database.
            if (value is string text && query.FilterTypes.TryGetValue(name, out var type) && SqlValues.ToSqlDbType(type) is { } dbType)
            {
                var converted = SqlValues.FromText(name, text, dbType);
                value = converted is DBNull ? null : converted;
            }
            all[name] = value;
        }

        var data = await execution.ExecuteQueryAsync(query.DataSql, all, execOptions, ct);

        int? total = null;
        if (request.IncludeTotal)
        {
            var count = await execution.ExecuteQueryAsync(query.CountSql, all, execOptions, ct);
            total = FirstCellAsInt(count);
        }

        return new DataSourceResult(data.ResultSets, total, data.RowsAffected, data.Duration) { Pages = true };
    }

    private async Task<DataSourceResult> RunQueryAsync(
        DataSourceDefinition def, IDictionary<string, object?> args, DataSourceRunRequest request,
        SqlExecOptions execOptions, CancellationToken ct)
    {
        var sql = def.Query!;

        // A Query statement is a read, and raw SQL can still write through a procedure (rakit_exec may execute every
        // procedure of the app schema, whitelisted or not), so whatever it changes is rolled back once it is read.
        if (execOptions.StatementType == StatementType.Query) execOptions = execOptions with { RollbackAfter = true };

        // DS-07 conventions are available to raw queries as native tokens; a mapped parameter of the same name wins.
        var all = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        all.TryAdd("Skip", request.Skip);
        all.TryAdd("Take", request.Take);
        all.TryAdd("OrderBy", request.OrderBy ?? "");
        all.TryAdd("Search", request.Search?.Trim() ?? "");
        all.TryAdd("IsCount", false);

        var result = await execution.ExecuteQueryAsync(sql, all, execOptions, ct);

        int? total = null;
        var rewritten = SqlTemplate.Rewrite(sql).Sql;
        var referenced = SqlTemplate.ReferencedParameters(rewritten);
        var declared = SqlTemplate.DeclaredVariables(rewritten);
        bool Reads(string name) => referenced.Contains(name, StringComparer.OrdinalIgnoreCase) && !declared.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (request.IncludeTotal && Reads("IsCount") && execOptions.StatementType == StatementType.Query)
        {
            var countArgs = new Dictionary<string, object?>(all, StringComparer.OrdinalIgnoreCase) { ["IsCount"] = true };
            total = FirstCellAsInt(await execution.ExecuteQueryAsync(sql, countArgs, execOptions, ct));
        }

        return new DataSourceResult(result.ResultSets, total, result.RowsAffected, result.Duration) { Pages = Reads("Skip") };
    }

    private async Task<DataSourceResult> RunProcedureAsync(
        DataSourceDefinition def, StatementType statement, IDictionary<string, object?> args, DataSourceRunRequest request,
        SqlExecOptions execOptions, CancellationToken ct)
    {
        var proc = def.Procedure!;

        // DS-07 conventions go in as arguments; the execution service only sends the ones the procedure declares and a
        // mapped parameter of the same name wins. OrderBy is left out when not asked for so the procedure's own default applies.
        var all = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase);
        all.TryAdd("Skip", request.Skip);
        all.TryAdd("Take", request.Take);
        all.TryAdd("Search", request.Search?.Trim() ?? "");
        all.TryAdd("IsCount", false);
        if (!string.IsNullOrWhiteSpace(request.OrderBy)) all.TryAdd("OrderBy", request.OrderBy);

        var result = await execution.ExecuteProcedureAsync(proc.Schema, proc.Name, all, execOptions, ct);

        // A list read (it asks for a total) also learns whether the procedure pages, from the parameters it declares.
        int? total = null;
        bool? pages = null;
        if (request.IncludeTotal && statement == StatementType.Query)
        {
            var declared = await schemaBrowser.GetProcedureParametersAsync(proc.Schema, proc.Name, ct);
            bool Declares(string name) => declared.Any(p => string.Equals(p.Name.TrimStart('@'), name, StringComparison.OrdinalIgnoreCase));
            pages = Declares("Skip");

            if (def.CountResultSet is { } countIndex)
            {
                total = countIndex < result.ResultSets.Count ? FirstCellAsInt(result.ResultSets[countIndex]) : null;
            }
            else if (Declares("IsCount"))
            {
                var countArgs = new Dictionary<string, object?>(all, StringComparer.OrdinalIgnoreCase) { ["IsCount"] = true };
                var counted = await execution.ExecuteProcedureAsync(proc.Schema, proc.Name, countArgs, execOptions, ct);
                total = counted.ResultSets.Count > 0 ? FirstCellAsInt(counted.ResultSets[0]) : null;
            }
        }

        return new DataSourceResult(result.ResultSets, total, result.RowsAffected, result.Duration)
        {
            OutputParameters = result.OutputParameters,
            ReturnValue = result.ReturnValue,
            ListResultSetIndex = def.ListResultSet,
            Pages = pages,
        };
    }

    private static int? FirstCellAsInt(SqlExecResult result)
        => result.ResultSets.Count > 0 ? FirstCellAsInt(result.ResultSets[0]) : null;

    private static int? FirstCellAsInt(DataTableResult set)
        => set.Rows.Count > 0 && set.Rows[0].Count > 0 && set.Rows[0][0] is { } cell
            ? Convert.ToInt32(cell, System.Globalization.CultureInfo.InvariantCulture)
            : null;
}
