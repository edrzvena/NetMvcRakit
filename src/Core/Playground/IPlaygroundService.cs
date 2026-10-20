namespace Rakit.Core.Playground;

/// <summary>
/// The Builder's SQL Playground over the one SQL Server Rakit uses. Every call except <see cref="Settings"/> throws
/// <see cref="PlaygroundDisabledException"/> while Rakit:Playground:Enabled is false. Objects outside Rakit:Db:AllowedSchemas
/// are never listed, described or scripted.
/// </summary>
public interface IPlaygroundService
{
    PlaygroundSettings Settings { get; }

    Task<IReadOnlyList<PlaygroundTreeSchema>> GetTreeAsync(CancellationToken ct = default);

    /// <summary>Throws SchemaAccessDeniedException / SchemaObjectNotFoundException for a schema or object that is not available.</summary>
    Task<PlaygroundObjectDetail> GetObjectAsync(string schema, string name, CancellationToken ct = default);

    /// <summary>
    /// Text for the editor. Kind: select, insert, update, delete, exec, create, definition, alter, addColumn, drop.
    /// An unknown kind, or one the object cannot have (insert on a view), is an ArgumentException.
    /// </summary>
    Task<string> ScriptAsync(string kind, string schema, string name, CancellationToken ct = default);

    /// <summary>CREATE TABLE text for the designer's form; ArgumentException says what is wrong with it.</summary>
    string ScriptNewTable(NewTableSpec spec);

    /// <summary>
    /// Runs a script (batches separated by GO). A SQL error is part of the result (<see cref="PlaygroundRunResult.Error"/>), not
    /// an exception; bad input is an ArgumentException and an unreachable database a SqlException.
    /// </summary>
    Task<PlaygroundRunResult> RunAsync(PlaygroundRunRequest request, CancellationToken ct = default);
}
