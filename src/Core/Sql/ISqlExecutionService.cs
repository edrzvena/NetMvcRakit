namespace Rakit.Core.Sql;

/// <summary>
/// One execution connection with one open transaction, shared by several statements (a workflow run that wants all its
/// database steps to commit or roll back together). Pass it in <see cref="SqlExecOptions.Scope"/>. The owner decides:
/// <see cref="CommitAsync"/> or <see cref="RollbackAsync"/>; disposing a scope that was not completed rolls it back.
/// </summary>
public interface ISqlScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync();
}

public interface ISqlExecutionService
{
    /// <summary>Opens an execution connection (login rakit_exec) and starts a transaction on it. The caller disposes the scope.</summary>
    /// <exception cref="SqlExecutionException">The database could not be reached.</exception>
    Task<ISqlScope> BeginScopeAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs a parameterised query on the Execution connection (login rakit_exec). <c>{name}</c> placeholders and
    /// native <c>@name</c> tokens are bound from <paramref name="args"/> (case-insensitive keys, leading @ optional).
    /// </summary>
    /// <exception cref="SqlTemplateException">Malformed template.</exception>
    /// <exception cref="ArgumentException">A placeholder has no value, or a value has an unsupported type.</exception>
    /// <exception cref="SqlExecutionException">The database rejected or timed out the statement.</exception>
    Task<SqlExecResult> ExecuteQueryAsync(
        string sqlTemplate,
        IDictionary<string, object?> args,
        SqlExecOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Calls a stored procedure on the Execution connection (login rakit_exec) with <c>CommandType.StoredProcedure</c>.
    /// Parameter types, sizes and directions come from the catalog; <paramref name="args"/> supplies input values by
    /// name (case-insensitive, leading @ optional) and unknown names are ignored. An input parameter without a value
    /// is skipped when the procedure declares a default, otherwise it is an error. OUTPUT parameters are always sent
    /// and returned in <see cref="SqlExecResult.OutputParameters"/>, the return value in <see cref="SqlExecResult.ReturnValue"/>.
    /// All result sets are read (each capped at <see cref="SqlExecOptions.MaxRows"/>); when one is cut short the
    /// remaining sets are skipped and the output parameters and return value are null.
    /// This method does not check the SP whitelist or the Allow* flags; the caller (data source service) does.
    /// </summary>
    /// <exception cref="ArgumentException">Blank name, a required parameter has no value, a value does not fit its parameter, or a parameter type is not supported (table-valued, CLR).</exception>
    /// <exception cref="Rakit.Core.Schema.SchemaAccessDeniedException">Schema is not in AllowedSchemas.</exception>
    /// <exception cref="Rakit.Core.Schema.SchemaObjectNotFoundException">No such procedure, or a system procedure (sp_*, xp_*).</exception>
    /// <exception cref="SqlExecutionException">The database rejected or timed out the call.</exception>
    Task<SqlExecResult> ExecuteProcedureAsync(
        string schema,
        string name,
        IDictionary<string, object?> args,
        SqlExecOptions options,
        CancellationToken ct = default);
}
