using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.DataSources;

namespace Rakit.Core.Workflows;

/// <param name="IsError">An error blocks publishing; a warning only tells the builder what will not work yet.</param>
public sealed record WorkflowIssue(bool IsError, string Message);

public interface IWorkflowChecker
{
    /// <summary>
    /// Static check of a workflow's steps against the database and the flags (nothing is executed): each step's data source
    /// exists; StepOutput parameters point at an EARLIER step with a key the runner produces ({code}.return,
    /// {code}.out.{Parameter}, {code}.row.{Column}); and a warning when a step's data source is switched off by a flag
    /// (AllowWrite, AllowRawQuery, AllowStoredProcedure) so it would be refused at runtime.
    /// </summary>
    Task<IReadOnlyList<WorkflowIssue>> CheckAsync(IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default);
}

public sealed class WorkflowChecker(IDataSourceRepository dataSources, IOptions<RakitDbOptions> options) : IWorkflowChecker
{
    private static readonly string[] OutputKinds = ["return", "out", "row"];

    public async Task<IReadOnlyList<WorkflowIssue>> CheckAsync(IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
    {
        var issues = new List<WorkflowIssue>();
        var flags = options.Value;
        var earlier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = new Dictionary<Guid, DataSource?>();

        foreach (var step in steps)
        {
            var label = $"Langkah '{step.DisplayName}'";

            if (!known.TryGetValue(step.DataSourceId, out var ds))
            {
                ds = step.DataSourceId == Guid.Empty ? null : await dataSources.GetAsync(step.DataSourceId, ct);
                known[step.DataSourceId] = ds;
            }

            if (ds is null)
            {
                // an unchosen data source is already reported by the structural validation ("belum dipilih")
                if (step.DataSourceId != Guid.Empty) issues.Add(new WorkflowIssue(true, $"{label}: data source tidak ditemukan."));
            }
            else
            {
                if (ds.StatementType == StatementType.Command && !flags.AllowWrite)
                    issues.Add(new WorkflowIssue(false, $"{label}: data source '{ds.Code}' bertipe Command dan Rakit:Db:AllowWrite = false, jadi langkah ini ditolak saat dijalankan."));
                if (ds.Mode == DataSourceMode.Query && !flags.AllowRawQuery)
                    issues.Add(new WorkflowIssue(false, $"{label}: data source '{ds.Code}' memakai query mentah dan Rakit:Db:AllowRawQuery = false."));
                if (ds.Mode == DataSourceMode.StoredProcedure && !flags.AllowStoredProcedure)
                    issues.Add(new WorkflowIssue(false, $"{label}: data source '{ds.Code}' memakai stored procedure dan Rakit:Db:AllowStoredProcedure = false."));

                foreach (var p in ds.Definition.Parameters.Where(p => p.Source == ParameterSource.StepOutput))
                {
                    var key = p.Key?.Trim() ?? "";
                    var parts = key.Split('.', 3);
                    var from = parts[0];
                    var kind = parts.Length > 1 ? parts[1] : "";

                    if (!earlier.Contains(from))
                        issues.Add(new WorkflowIssue(true, $"{label}: parameter @{p.Name} membaca '{key}', tetapi tidak ada langkah '{from}' sebelum langkah ini."));
                    else if (!OutputKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)
                             || (kind.Equals("return", StringComparison.OrdinalIgnoreCase) ? parts.Length != 2 : parts.Length != 3 || parts[2].Length == 0))
                        issues.Add(new WorkflowIssue(true, $"{label}: parameter @{p.Name} memakai key '{key}' yang tidak dikenal; gunakan '{from}.return', '{from}.out.NamaParameter', atau '{from}.row.NamaKolom'."));
                }
            }

            earlier.Add(step.Code);
        }
        return issues;
    }
}
