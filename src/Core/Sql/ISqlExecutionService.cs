namespace Rakit.Core.Sql;

public interface ISqlExecutionService
{
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
}
