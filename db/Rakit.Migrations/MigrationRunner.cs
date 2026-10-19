using System.Reflection;
using DbUp;
using DbUp.Engine;

namespace Rakit.Migrations;

public static class MigrationRunner
{
    /// <summary>Creates the database if missing, then applies embedded scripts in name order.</summary>
    public static DatabaseUpgradeResult Run(string connectionString, Action<string>? log = null)
    {
        EnsureDatabase.For.SqlDatabase(connectionString);

        var builder = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .JournalToSqlTable("dbo", "SchemaVersions");

        if (log is not null)
            builder = builder.LogTo(new DelegateLog(log));
        else
            builder = builder.LogToNowhere();

        return builder.Build().PerformUpgrade();
    }

    private sealed class DelegateLog(Action<string> write) : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }
        public void LogDebug(string format, params object[] args) { }
        public void LogInformation(string format, params object[] args) => write(string.Format(format, args));
        public void LogWarning(string format, params object[] args) => write("WARN: " + string.Format(format, args));
        public void LogError(string format, params object[] args) => write("ERROR: " + string.Format(format, args));
        public void LogError(Exception ex, string format, params object[] args) => write("ERROR: " + string.Format(format, args) + " " + ex.Message);
    }
}
