using System.Text.RegularExpressions;

namespace Rakit.Core.DataSources;

/// <summary>
/// Structural validation only. Whether a table/column really exists, or whether raw queries are allowed,
/// is checked later against the catalog and configuration flags.
/// </summary>
public static partial class DataSourceInputValidator
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{1,49}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    public static partial Regex ParameterNamePattern();

    public static IReadOnlyList<string> Validate(DataSourceInput input)
    {
        var errors = new List<string>();

        if (!CodePattern().IsMatch(input.Code ?? ""))
            errors.Add("Code harus 2-50 karakter: diawali huruf, lalu huruf/angka/_/-.");

        if (string.IsNullOrWhiteSpace(input.Name))
            errors.Add("Name wajib diisi.");
        else if (input.Name.Length > 200)
            errors.Add("Name maksimal 200 karakter.");

        var def = input.Definition;
        if (def is null)
        {
            errors.Add("Definition wajib ada.");
            return errors;
        }

        if (def.TimeoutSeconds is < 1 or > DataSourceDefinition.MaxTimeoutSeconds)
            errors.Add($"Timeout harus antara 1 dan {DataSourceDefinition.MaxTimeoutSeconds} detik.");

        switch (input.Mode)
        {
            case DataSourceMode.Table:
                if (input.StatementType != StatementType.Query)
                    errors.Add("Mode Table hanya mendukung statement Query.");
                if (def.Table is null || string.IsNullOrWhiteSpace(def.Table.Schema) || string.IsNullOrWhiteSpace(def.Table.Name))
                    errors.Add("Mode Table membutuhkan schema dan nama tabel.");
                break;
            case DataSourceMode.Query:
                if (string.IsNullOrWhiteSpace(def.Query))
                    errors.Add("Mode Query membutuhkan teks query.");
                break;
            default:
                errors.Add("Mode Stored Procedure belum didukung.");
                break;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in def.Parameters)
        {
            if (!ParameterNamePattern().IsMatch(p.Name ?? ""))
                errors.Add($"Nama parameter '{p.Name}' tidak valid (huruf/angka/_, tidak diawali angka).");
            else if (!names.Add(p.Name!))
                errors.Add($"Nama parameter '{p.Name}' dipakai lebih dari sekali.");
        }

        return errors;
    }
}
