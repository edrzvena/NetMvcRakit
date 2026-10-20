using Rakit.Core.Schema;

namespace Rakit.Core.Procedures;

public interface ISpWhitelistService
{
    /// <summary>Every whitelist row, active or not, ordered by schema then name.</summary>
    Task<IReadOnlyList<SpWhitelistEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// True only when the schema is in AllowedSchemas and an ACTIVE whitelist row exists (names are case-insensitive).
    /// Does not check that the procedure still exists in the catalog; discovery and execution do.
    /// </summary>
    Task<bool> IsAllowedAsync(string schema, string name, CancellationToken ct = default);

    /// <summary>
    /// Activates or deactivates a procedure, adding it to the whitelist if it is not listed yet. The stored spelling
    /// is the catalog's. A listed row whose procedure no longer exists can still be deactivated (not activated).
    /// </summary>
    /// <exception cref="ArgumentException">schema or name is null/blank.</exception>
    /// <exception cref="SchemaAccessDeniedException">schema is not in AllowedSchemas.</exception>
    /// <exception cref="SchemaObjectNotFoundException">no such procedure (and, for a deactivation, no such row either), or a system procedure (sp_*, xp_*).</exception>
    Task<SpWhitelistEntry> SetActiveAsync(string schema, string name, bool isActive, string user, CancellationToken ct = default);
}
