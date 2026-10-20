namespace Rakit.Web.Areas.Admin.Models;

public enum SpWhitelistStatus
{
    /// <summary>Whitelisted and active: data sources may use it.</summary>
    Active,
    /// <summary>Listed but switched off.</summary>
    Inactive,
    /// <summary>Exists in an allowed schema but was never listed (default-deny).</summary>
    NotListed,
    /// <summary>Listed, but the procedure no longer exists in the catalog or its schema is no longer allowed.</summary>
    Missing,
}

/// <param name="IsActive">The row's own switch (false for NotListed).</param>
/// <param name="CanToggle">False for a Missing row that is already off, or whose schema is no longer allowed (the service refuses it).</param>
public sealed record SpWhitelistRow(
    string Schema, string Name, SpWhitelistStatus Status, bool IsActive, DateTime? Updated, bool CanToggle);

public sealed record SpWhitelistIndexViewModel(
    IReadOnlyList<SpWhitelistRow> Rows, IReadOnlyList<string> AllowedSchemas, string? Message, string? Error);
