using System.Text.RegularExpressions;

namespace Rakit.Core.Workflows;

/// <summary>Structural validation only; whether a step's data source exists is checked by <see cref="IWorkflowChecker"/>.</summary>
public static partial class WorkflowInputValidator
{
    public const int MaxCodeLength = 50;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 500;
    public const int MaxSteps = 20;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{1,49}$")]
    private static partial Regex WorkflowCodePattern();

    // no '-' or '.': the step code is the first part of a StepOutput key ("create.row.Id")
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,49}$")]
    private static partial Regex StepCodePattern();

    public static bool IsValidStepCode(string? code) => code is not null && StepCodePattern().IsMatch(code);

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
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var code = step.Code?.Trim() ?? "";
            var label = $"Langkah {i + 1}" + (code.Length > 0 ? $" ('{code}')" : "");

            if (!StepCodePattern().IsMatch(code))
                errors.Add($"{label}: kode harus 1-{WorkflowStep.MaxCodeLength} karakter, huruf/angka/'_', diawali huruf.");
            else if (!codes.Add(code))
                errors.Add($"{label}: kode dipakai lebih dari sekali.");

            if (step.Name is { Length: > WorkflowStep.MaxNameLength })
                errors.Add($"{label}: nama maksimal {WorkflowStep.MaxNameLength} karakter.");
            if (!Enum.IsDefined(step.Type))
                errors.Add($"{label}: tipe langkah tidak dikenal.");
            if (step.DataSourceId == Guid.Empty)
                errors.Add($"{label}: data source belum dipilih.");
        }

        return errors;
    }
}
