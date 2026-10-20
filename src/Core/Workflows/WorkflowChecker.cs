using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.DataSources;
using Rakit.Core.Modules;

namespace Rakit.Core.Workflows;

/// <param name="IsError">An error blocks publishing; a warning only tells the builder what will not work (yet) or what to be careful with.</param>
public sealed record WorkflowIssue(bool IsError, string Message);

public interface IWorkflowChecker
{
    /// <summary>
    /// Static check of a workflow's steps against the database and the settings (nothing is executed). Errors: a step's data source
    /// or module does not exist; a Rest URL's host is not in <c>Rakit:Workflow:AllowedHosts</c>; a StepOutput mapping or a
    /// <c>{{placeholder}}</c> reads the output of a step that does not run before it, or with a shape that step does not produce.
    /// Warnings: a data source switched off by a flag, SMTP not configured, a draft target module, steps that cannot be reached
    /// from the first step, and what <paramref name="transaction"/> cannot undo.
    /// </summary>
    Task<IReadOnlyList<WorkflowIssue>> CheckAsync(IReadOnlyList<WorkflowStep> steps, bool transaction = false, CancellationToken ct = default);
}

public sealed class WorkflowChecker(
    IDataSourceRepository dataSources, IModuleRepository modules, IOptions<RakitDbOptions> db,
    IOptions<WorkflowOptions> workflowOptions, IOptions<MailOptions> mail, IWorkflowScriptEngine scripts) : IWorkflowChecker
{
    public async Task<IReadOnlyList<WorkflowIssue>> CheckAsync(
        IReadOnlyList<WorkflowStep> steps, bool transaction = false, CancellationToken ct = default)
    {
        var issues = new List<WorkflowIssue>();
        var flags = db.Value;
        var index = WorkflowGraph.Index(steps);
        var ancestors = WorkflowGraph.AncestorsOf(steps);
        var knownSources = new Dictionary<Guid, DataSource?>();

        // steps nobody links to (the first step is the entry)
        var entry = WorkflowGraph.EntryOf(steps);
        if (entry is not null)
        {
            var reachable = WorkflowGraph.ReachableFrom(steps, entry, includeStart: true);
            foreach (var step in steps.Where(s => !reachable.Contains(s.Code.Trim())))
                issues.Add(new WorkflowIssue(false, $"Langkah '{step.DisplayName}' tidak tersambung dari langkah pertama, jadi tidak pernah dijalankan."));
        }

        foreach (var step in steps)
        {
            var label = $"Langkah '{step.DisplayName}'";
            var before = ancestors.GetValueOrDefault(step.Code.Trim()) ?? [];

            switch (step.Type)
            {
                case StepType.DataSource:
                {
                    if (!knownSources.TryGetValue(step.DataSourceId, out var ds))
                    {
                        ds = step.DataSourceId == Guid.Empty ? null : await dataSources.GetAsync(step.DataSourceId, ct);
                        knownSources[step.DataSourceId] = ds;
                    }
                    if (ds is null)
                    {
                        // an unchosen data source is already reported by the structural validation ("belum dipilih")
                        if (step.DataSourceId != Guid.Empty) issues.Add(new WorkflowIssue(true, $"{label}: data source tidak ditemukan."));
                        break;
                    }

                    if (ds.StatementType == StatementType.Command && !flags.AllowWrite)
                        issues.Add(new WorkflowIssue(false, $"{label}: data source '{ds.Code}' bertipe Command dan Rakit:Db:AllowWrite = false, jadi langkah ini ditolak saat dijalankan."));
                    if (ds.Mode == DataSourceMode.Query && !flags.AllowRawQuery)
                        issues.Add(new WorkflowIssue(false, $"{label}: data source '{ds.Code}' memakai query mentah dan Rakit:Db:AllowRawQuery = false."));
                    if (ds.Mode == DataSourceMode.StoredProcedure && !flags.AllowStoredProcedure)
                        issues.Add(new WorkflowIssue(false, $"{label}: data source '{ds.Code}' memakai stored procedure dan Rakit:Db:AllowStoredProcedure = false."));

                    foreach (var p in ds.Definition.Parameters.Where(p => p.Source == ParameterSource.StepOutput))
                        CheckReference(label, $"parameter @{p.Name} membaca", p.Key?.Trim() ?? "", before, index, issues);
                    break;
                }
                case StepType.Rest when step.Rest is { } rest:
                {
                    var authority = WorkflowInputValidator.AuthorityOf(rest.Url ?? "");
                    if (authority is { Length: > 0 } && !authority.Contains('{')
                        && Uri.TryCreate((rest.Url!.Trim().StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "https://" : "http://") + authority, UriKind.Absolute, out var uri)
                        && !workflowOptions.Value.IsAllowed(uri))
                        issues.Add(new WorkflowIssue(true, $"{label}: host '{authority}' tidak ada di Rakit:Workflow:AllowedHosts, jadi panggilannya akan ditolak."));
                    CheckTemplates(label, [rest.Url, .. rest.Headers.Select(h => h.Value), rest.Body], before, index, issues);
                    break;
                }
                case StepType.Email when step.Email is { } email:
                {
                    if (!mail.Value.IsConfigured)
                        issues.Add(new WorkflowIssue(false, $"{label}: SMTP belum dikonfigurasi (Rakit:Mail), jadi email hanya dicatat di log dan tracking, tidak dikirim."));
                    CheckTemplates(label, [email.To, email.Subject, email.Body], before, index, issues);
                    break;
                }
                case StepType.Page when step.Page is { } page:
                    CheckTemplates(label, [page.Html], before, index, issues);
                    break;
                case StepType.Module when step.Module is { } module:
                {
                    if (module.ModuleId != Guid.Empty)
                    {
                        var target = await modules.GetAsync(module.ModuleId, ct);
                        if (target is null) issues.Add(new WorkflowIssue(true, $"{label}: module tujuan tidak ditemukan."));
                        else if (target.Status != ModuleStatus.Published)
                            issues.Add(new WorkflowIssue(false, $"{label}: module tujuan '{target.Name}' masih Draft; langkah ini gagal sampai module dipublikasikan."));
                    }
                    CheckTemplates(label, module.Parameters.Select(p => p.Value), before, index, issues);
                    break;
                }
                case StepType.Condition when step.Condition is { } condition:
                    CheckTemplates(label, [condition.Left, condition.Right], before, index, issues);
                    break;
                case StepType.Javascript when step.Script is { } script:
                {
                    if (!string.IsNullOrWhiteSpace(script.Source) && scripts.CheckSyntax(script.Source) is { } syntax)
                        issues.Add(new WorkflowIssue(true, $"{label}: skrip tidak valid: {syntax}"));
                    break;
                }
                case StepType.Action when step.Action is { } action:
                {
                    CheckTemplates(label, [action.Title, action.Intro], before, index, issues);
                    if (action.Buttons.Count > 0 && action.Buttons.All(b => b.Cancel) && !string.IsNullOrWhiteSpace(step.Next))
                        issues.Add(new WorkflowIssue(false, $"{label}: semua tombolnya mengakhiri workflow, jadi langkah sesudahnya tidak pernah dijalankan."));
                    break;
                }
            }
        }

        if (transaction)
        {
            var dbSteps = steps.Where(s => s.Type == StepType.DataSource).ToList();
            if (dbSteps.Count == 0)
                issues.Add(new WorkflowIssue(false, "Opsi satu transaksi tidak berpengaruh: tidak ada langkah data source."));
            foreach (var external in steps.Where(s => s.Type is StepType.Rest or StepType.Email))
                if (dbSteps.Any(d => ancestors.GetValueOrDefault(d.Code.Trim())?.Contains(external.Code.Trim()) == true))
                    issues.Add(new WorkflowIssue(false,
                        $"Langkah '{external.DisplayName}' ({external.Type}) berjalan sebelum langkah database dalam transaksi yang sama: hasilnya tidak bisa dibatalkan bila langkah database gagal, dan transaksi tetap terbuka selama panggilannya."));
        }
        return issues;
    }

    private static void CheckTemplates(
        string label, IEnumerable<string?> texts, HashSet<string> before, Dictionary<string, WorkflowStep> index, List<WorkflowIssue> issues)
    {
        foreach (var key in texts.SelectMany(t => WorkflowTemplate.Keys(t)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!WorkflowTemplate.Roots.Contains(WorkflowTemplate.RootOf(key), StringComparer.OrdinalIgnoreCase))
                CheckReference(label, $"placeholder {{{{{key}}}}} membaca", key, before, index, issues);
    }

    /// <summary>An output key (<c>create.row.Id</c>): the step must run before this one and produce that shape.</summary>
    private static void CheckReference(
        string label, string what, string key, HashSet<string> before, Dictionary<string, WorkflowStep> index, List<WorkflowIssue> issues)
    {
        var parts = key.Split('.', 3);
        var from = parts[0];
        if (!before.Contains(from) || !index.TryGetValue(from, out var source))
        {
            issues.Add(new WorkflowIssue(true, $"{label}: {what} '{key}', tetapi tidak ada langkah '{from}' sebelum langkah ini."));
            return;
        }

        var kind = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        var ok = source.Type switch
        {
            StepType.DataSource => (kind == "return" && parts.Length == 2) || (kind is "out" or "row" && parts.Length == 3 && parts[2].Length > 0),
            StepType.Rest => (kind is "status" or "body" && parts.Length == 2) || (kind == "json" && parts.Length == 3 && parts[2].Length > 0),
            StepType.Email => kind == "sent" && parts.Length == 2,
            StepType.Javascript => (kind == "body" && parts.Length == 2) || (kind == "json" && parts.Length == 3 && parts[2].Length > 0),
            StepType.Action => (kind == "button" && parts.Length == 2) || (kind == "form" && parts.Length == 3 && parts[2].Length > 0),
            _ => false,
        };
        if (ok) return;

        var shapes = source.Type switch
        {
            StepType.DataSource => $"'{from}.return', '{from}.out.NamaParameter', atau '{from}.row.NamaKolom'",
            StepType.Rest => $"'{from}.status', '{from}.body', atau '{from}.json.jalur'",
            StepType.Email => $"'{from}.sent'",
            StepType.Javascript => $"'{from}.body' atau '{from}.json.jalur'",
            StepType.Action => $"'{from}.button' atau '{from}.form.NamaField'",
            _ => "",
        };
        issues.Add(new WorkflowIssue(true, shapes.Length == 0
            ? $"{label}: {what} '{key}', tetapi langkah '{from}' ({source.Type}) tidak menghasilkan output."
            : $"{label}: {what} '{key}' yang bentuknya tidak dikenal untuk langkah {source.Type}; gunakan {shapes}."));
    }
}
