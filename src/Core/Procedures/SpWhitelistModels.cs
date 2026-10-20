namespace Rakit.Core.Procedures;

public sealed record SpWhitelistEntry(
    Guid Id, string Schema, string Name, bool IsActive, DateTime Created, DateTime? Updated);
