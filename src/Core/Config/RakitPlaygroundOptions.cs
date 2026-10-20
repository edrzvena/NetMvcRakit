namespace Rakit.Core.Config;

/// <summary>Settings of the Builder Playground (<c>Rakit:Playground</c>). Everything is off or read-only unless switched on.</summary>
public sealed class RakitPlaygroundOptions
{
    public const string SectionName = "Rakit:Playground";

    public const int MaxRowsLimit = 5000;
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Off by default: the Playground runs SQL typed in the browser and the site has no login.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Off = every run is read-only (it runs as a database user that can only read and execute, inside a transaction that is
    /// always rolled back). On = scripts run as a user that controls the app schema (DML and DDL) and may be committed.
    /// </summary>
    public bool AllowWrite { get; set; }

    public int MaxRows { get; set; } = 1000;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxScriptLength { get; set; } = 200_000;
}
