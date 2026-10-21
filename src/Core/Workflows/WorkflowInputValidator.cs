using System.Text.RegularExpressions;
using Rakit.Core.Modules;

namespace Rakit.Core.Workflows;

/// <summary>
/// Structural validation only (shapes, limits, links, loops); whether a data source or module exists, which hosts a Rest step may
/// call and whether the outputs a step reads come from an earlier step is checked by <see cref="IWorkflowChecker"/>.
/// </summary>
public static partial class WorkflowInputValidator
{
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 500;
    public const int MaxSteps = 30;

    /// <summary>Headers a Rest step may not set (the transport owns them).</summary>
    public static readonly IReadOnlyList<string> BlockedHeaders =
        ["Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Expect", "TE", "Trailer", "Proxy-Authorization"];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{1,49}$")]
    private static partial Regex WorkflowCodePattern();

    // no '-' or '.': the step code is the first part of a placeholder key ("create.row.Id")
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,49}$")]
    private static partial Regex StepCodePattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,29}$")]
    private static partial Regex ButtonCodePattern();

    [GeneratedRegex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]{1,100}$")]
    private static partial Regex HeaderNamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,49}$")]
    private static partial Regex ParameterNamePattern();

    public static bool IsValidStepCode(string? code) => code is not null && StepCodePattern().IsMatch(code);

    /// <summary>The scheme://authority part of a Rest URL, or null when the text does not start with http:// or https://.</summary>
    public static string? AuthorityOf(string url)
    {
        var text = url.Trim();
        var scheme = text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
        if (scheme == 0) return null;
        var end = text.IndexOfAny(['/', '?', '#'], scheme);
        return end < 0 ? text[scheme..] : text[scheme..end];
    }

    public static IReadOnlyList<string> Validate(WorkflowInput input)
    {
        var errors = new List<string>();

        if (!WorkflowCodePattern().IsMatch(input.Code?.Trim() ?? ""))
            errors.Add("Code harus 2-50 karakter: huruf, angka, '_' atau '-', diawali huruf.");

        if (string.IsNullOrWhiteSpace(input.Name))
            errors.Add("Name wajib diisi.");
        else if (input.Name.Trim().Length > MaxNameLength)
            errors.Add($"Name maksimal {MaxNameLength} karakter.");

        if (input.Description is { Length: > MaxDescriptionLength })
            errors.Add($"Description maksimal {MaxDescriptionLength} karakter.");

        var steps = input.Steps ?? [];
        if (steps.Count == 0) errors.Add("Workflow membutuhkan setidaknya satu langkah.");
        if (steps.Count > MaxSteps) errors.Add($"Workflow maksimal {MaxSteps} langkah.");

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var code = step.Code?.Trim() ?? "";
            if (StepCodePattern().IsMatch(code)) codes.Add(code);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var code = step.Code?.Trim() ?? "";
            var label = $"Langkah {i + 1}" + (code.Length > 0 ? $" ('{code}')" : "");

            if (!StepCodePattern().IsMatch(code))
                errors.Add($"{label}: kode harus 1-{WorkflowStep.MaxCodeLength} karakter, huruf/angka/'_', diawali huruf.");
            else if (WorkflowTemplate.Roots.Contains(code, StringComparer.OrdinalIgnoreCase))
                // {{form.row.Id}} would read the form field "row.Id", never this step's output
                errors.Add($"{label}: kode '{code}' sudah dipakai placeholder ({string.Join(", ", WorkflowTemplate.Roots)}); pilih kode lain.");
            else if (!seen.Add(code))
                errors.Add($"{label}: kode dipakai lebih dari sekali.");

            if (step.Name is { Length: > WorkflowStep.MaxNameLength })
                errors.Add($"{label}: nama maksimal {WorkflowStep.MaxNameLength} karakter.");

            if (!Enum.IsDefined(step.Type))
            {
                errors.Add($"{label}: tipe langkah tidak dikenal.");
                continue;
            }

            ValidateStep(step, label, errors);

            if (step.Type == StepType.Condition)
            {
                if (!string.IsNullOrWhiteSpace(step.Next)) errors.Add($"{label}: langkah Kondisi tidak memakai 'next'; isi cabang ya/tidak.");
            }
            foreach (var link in WorkflowGraph.LinksOf(step))
            {
                if (!codes.Contains(link)) errors.Add($"{label}: tersambung ke langkah '{link}' yang tidak ada.");
                else if (link.Equals(code, StringComparison.OrdinalIgnoreCase)) errors.Add($"{label}: tidak bisa tersambung ke dirinya sendiri.");
            }
        }

        if (input.Transaction && steps.Any(s => s.Type == StepType.Action))
            errors.Add("Langkah Form (Action) tidak bisa dipakai bersama opsi satu transaksi: transaksi tidak bisa terbuka sampai pengunjung menjawab.");

        if (errors.Count == 0 && WorkflowGraph.HasCycle(steps))
            errors.Add("Langkah-langkah saling berputar (loop): sebuah langkah akhirnya kembali ke langkah sebelumnya.");

        return errors;
    }

    private static void ValidateStep(WorkflowStep step, string label, List<string> errors)
    {
        switch (step.Type)
        {
            case StepType.DataSource:
                if (step.DataSourceId == Guid.Empty) errors.Add($"{label}: data source belum dipilih.");
                break;
            case StepType.Rest:
                ValidateRest(step.Rest, label, errors);
                break;
            case StepType.Email:
                ValidateEmail(step.Email, label, errors);
                break;
            case StepType.Page:
                if (step.Page is null || string.IsNullOrWhiteSpace(step.Page.Html)) errors.Add($"{label}: isi halaman (HTML) wajib diisi.");
                else if (step.Page.Html.Length > PageStepConfig.MaxHtmlLength) errors.Add($"{label}: isi halaman maksimal {PageStepConfig.MaxHtmlLength} karakter.");
                break;
            case StepType.Module:
                ValidateModule(step.Module, label, errors);
                break;
            case StepType.Condition:
                ValidateCondition(step.Condition, label, errors);
                break;
            case StepType.Javascript:
                ValidateScript(step.Script, label, errors);
                break;
            case StepType.Action:
                ValidateAction(step.Action, label, errors);
                break;
        }
    }

    private static void ValidateScript(ScriptStepConfig? script, string label, List<string> errors)
    {
        if (script is null || string.IsNullOrWhiteSpace(script.Source)) errors.Add($"{label}: skrip wajib diisi.");
        else if (script.Source.Length > ScriptStepConfig.MaxLength) errors.Add($"{label}: skrip maksimal {ScriptStepConfig.MaxLength} karakter.");
    }

    private static void ValidateAction(ActionStepConfig? action, string label, List<string> errors)
    {
        if (action is null)
        {
            errors.Add($"{label}: pengaturan Form wajib ada.");
            return;
        }

        if (string.IsNullOrWhiteSpace(action.Title)) errors.Add($"{label}: judul form wajib diisi.");
        else if (action.Title.Trim().Length > ActionStepConfig.MaxTitleLength) errors.Add($"{label}: judul form maksimal {ActionStepConfig.MaxTitleLength} karakter.");
        if (action.Intro is { Length: > ActionStepConfig.MaxIntroLength }) errors.Add($"{label}: teks pengantar maksimal {ActionStepConfig.MaxIntroLength} karakter.");

        var fields = action.Fields ?? [];
        if (fields.Count > ActionStepConfig.MaxFields) errors.Add($"{label}: maksimal {ActionStepConfig.MaxFields} field.");
        else foreach (var message in ModuleInputValidator.ValidateStandaloneFields(fields)) errors.Add($"{label}: {message}");

        var buttons = action.Buttons ?? [];
        if (buttons.Count == 0) errors.Add($"{label}: form membutuhkan setidaknya satu tombol.");
        else if (buttons.Count > ActionStepConfig.MaxButtons) errors.Add($"{label}: maksimal {ActionStepConfig.MaxButtons} tombol.");

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in buttons.Take(ActionStepConfig.MaxButtons))
        {
            var code = button.Code?.Trim() ?? "";
            if (!ButtonCodePattern().IsMatch(code)) errors.Add($"{label}: kode tombol '{code}' harus 1-{ActionButton.MaxCodeLength} karakter: huruf, angka, '_', diawali huruf.");
            else if (!codes.Add(code)) errors.Add($"{label}: kode tombol '{code}' dipakai lebih dari sekali.");

            var text = button.Label?.Trim() ?? "";
            if (text.Length == 0) errors.Add($"{label}: tombol '{code}' membutuhkan label.");
            else if (text.Length > ActionButton.MaxLabelLength) errors.Add($"{label}: label tombol '{code}' maksimal {ActionButton.MaxLabelLength} karakter.");
            if (!string.IsNullOrWhiteSpace(button.Style) && !ModuleAction.Styles.Contains(button.Style.Trim(), StringComparer.OrdinalIgnoreCase))
                errors.Add($"{label}: warna tombol '{code}' harus salah satu dari {string.Join(", ", ModuleAction.Styles)}.");
        }
    }

    private static void ValidateRest(RestStepConfig? rest, string label, List<string> errors)
    {
        if (rest is null)
        {
            errors.Add($"{label}: pengaturan Rest wajib ada.");
            return;
        }

        if (!RestStepConfig.Methods.Contains(rest.Method?.Trim().ToUpperInvariant() ?? ""))
            errors.Add($"{label}: method harus salah satu dari {string.Join(", ", RestStepConfig.Methods)}.");

        var url = rest.Url?.Trim() ?? "";
        if (url.Length == 0) errors.Add($"{label}: URL wajib diisi.");
        else if (url.Length > RestStepConfig.MaxUrlLength) errors.Add($"{label}: URL maksimal {RestStepConfig.MaxUrlLength} karakter.");
        else if (AuthorityOf(url) is not { Length: > 0 } authority) errors.Add($"{label}: URL harus diawali http:// atau https:// diikuti nama host.");
        else if (WorkflowTemplate.HasPlaceholders(authority) || authority.Contains('{')) errors.Add($"{label}: host pada URL harus ditulis lengkap (tanpa {{{{placeholder}}}}); placeholder hanya untuk path, query, header, dan body.");
        else if (authority.Contains('@')) errors.Add($"{label}: URL tidak boleh memuat user:password; pakai header.");

        if ((rest.Headers?.Count ?? 0) > RestStepConfig.MaxHeaders) errors.Add($"{label}: maksimal {RestStepConfig.MaxHeaders} header.");
        foreach (var header in rest.Headers ?? [])
        {
            var name = header.Name?.Trim() ?? "";
            if (!HeaderNamePattern().IsMatch(name)) errors.Add($"{label}: nama header '{name}' tidak valid.");
            else if (BlockedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)) errors.Add($"{label}: header '{name}' tidak boleh diatur.");
            if (header.Value?.Length > RestStepConfig.MaxHeaderValueLength) errors.Add($"{label}: nilai header '{name}' maksimal {RestStepConfig.MaxHeaderValueLength} karakter.");
        }

        if (rest.Body?.Length > RestStepConfig.MaxBodyLength) errors.Add($"{label}: body maksimal {RestStepConfig.MaxBodyLength} karakter.");
        if (!string.IsNullOrEmpty(rest.Body) && string.Equals(rest.Method?.Trim(), "GET", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{label}: method GET tidak memakai body.");
        if (!string.IsNullOrWhiteSpace(rest.ContentType) && (rest.ContentType.Length > 100 || !rest.ContentType.Contains('/')))
            errors.Add($"{label}: content type tidak valid (contoh application/json).");
        if (rest.TimeoutSeconds is < RestStepConfig.MinTimeoutSeconds or > RestStepConfig.MaxTimeoutSeconds)
            errors.Add($"{label}: timeout harus {RestStepConfig.MinTimeoutSeconds}-{RestStepConfig.MaxTimeoutSeconds} detik.");
        if (rest.Retries is < 0 or > RestStepConfig.MaxRetries)
            errors.Add($"{label}: percobaan ulang harus 0-{RestStepConfig.MaxRetries}.");
    }

    private static void ValidateEmail(EmailStepConfig? email, string label, List<string> errors)
    {
        if (email is null)
        {
            errors.Add($"{label}: pengaturan Email wajib ada.");
            return;
        }
        if (string.IsNullOrWhiteSpace(email.To)) errors.Add($"{label}: penerima (Kepada) wajib diisi.");
        else if (email.To.Length > EmailStepConfig.MaxFieldLength) errors.Add($"{label}: penerima maksimal {EmailStepConfig.MaxFieldLength} karakter.");
        if (string.IsNullOrWhiteSpace(email.Subject)) errors.Add($"{label}: subjek wajib diisi.");
        else if (email.Subject.Length > EmailStepConfig.MaxSubjectLength) errors.Add($"{label}: subjek maksimal {EmailStepConfig.MaxSubjectLength} karakter.");
        if (string.IsNullOrWhiteSpace(email.Body)) errors.Add($"{label}: isi email wajib diisi.");
        else if (email.Body.Length > EmailStepConfig.MaxBodyLength) errors.Add($"{label}: isi email maksimal {EmailStepConfig.MaxBodyLength} karakter.");
    }

    private static void ValidateModule(ModuleStepConfig? module, string label, List<string> errors)
    {
        if (module is null || module.ModuleId == Guid.Empty)
        {
            errors.Add($"{label}: module tujuan belum dipilih.");
            return;
        }
        if (module.Parameters.Count > ModuleStepConfig.MaxParameters) errors.Add($"{label}: maksimal {ModuleStepConfig.MaxParameters} parameter.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in module.Parameters)
        {
            var name = p.Name?.Trim() ?? "";
            if (!ParameterNamePattern().IsMatch(name)) errors.Add($"{label}: nama parameter '{name}' tidak valid (huruf, angka, '_').");
            else if (!names.Add(name)) errors.Add($"{label}: parameter '{name}' dipakai lebih dari sekali.");
            if (p.Value?.Length > ModuleStepConfig.MaxValueLength) errors.Add($"{label}: nilai parameter '{name}' maksimal {ModuleStepConfig.MaxValueLength} karakter.");
        }
    }

    private static void ValidateCondition(ConditionStepConfig? condition, string label, List<string> errors)
    {
        if (condition is null)
        {
            errors.Add($"{label}: pengaturan Kondisi wajib ada.");
            return;
        }
        if (string.IsNullOrWhiteSpace(condition.Left)) errors.Add($"{label}: nilai kiri wajib diisi.");
        else if (condition.Left.Length > ConditionStepConfig.MaxOperandLength) errors.Add($"{label}: nilai kiri maksimal {ConditionStepConfig.MaxOperandLength} karakter.");

        if (!Enum.IsDefined(condition.Operator)) errors.Add($"{label}: operator tidak dikenal.");
        else if (ConditionStepConfig.NeedsRight(condition.Operator) && string.IsNullOrWhiteSpace(condition.Right)) errors.Add($"{label}: nilai kanan wajib diisi untuk operator ini.");
        if (condition.Right?.Length > ConditionStepConfig.MaxOperandLength) errors.Add($"{label}: nilai kanan maksimal {ConditionStepConfig.MaxOperandLength} karakter.");
    }
}
