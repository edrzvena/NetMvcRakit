using System.Text.RegularExpressions;
using Rakit.Core.DataSources;

namespace Rakit.Core.Modules;

/// <summary>
/// Structural validation only. Whether the data source exists, or a menu's module / parent exist, is checked against
/// the database by the repositories and the screens.
/// </summary>
public static partial class ModuleInputValidator
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 500;
    public const int MaxFieldLength = 128;
    public const int MaxLabelLength = 200;
    public const int MaxRoleLength = 100;
    public const int MaxFormFields = 50;
    public const int MaxFieldTextLength = 4000;
    public const int MaxRegexLength = 500;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex FieldNamePattern();

    public static IReadOnlyList<string> Validate(ModuleInput input)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(input.Name))
            errors.Add("Name wajib diisi.");
        else if (input.Name.Trim().Length > MaxNameLength)
            errors.Add($"Name maksimal {MaxNameLength} karakter.");

        if (input.Description is { Length: > MaxDescriptionLength })
            errors.Add($"Description maksimal {MaxDescriptionLength} karakter.");

        if (!Enum.IsDefined(input.Type))
            errors.Add("Tipe module tidak dikenal.");

        var config = input.Config;
        if (config is null)
        {
            errors.Add("Config wajib ada.");
            return errors;
        }

        if (input.Type == ModuleType.Table)
        {
            if (config.Source?.List is null || config.Source.List.DataSourceId == Guid.Empty)
                errors.Add("Module Table membutuhkan data source untuk list.");
        }

        ValidateDisplay(config.Display, errors);
        ValidateRecordActions(config, errors);

        if (config.Access?.View is { } view && view.Any(r => string.IsNullOrWhiteSpace(r) || r.Trim().Length > MaxRoleLength))
            errors.Add($"Akses view berisi entri kosong atau lebih dari {MaxRoleLength} karakter.");

        return errors;
    }

    private static void ValidateDisplay(ModuleDisplay? display, List<string> errors)
    {
        if (display is null)
        {
            errors.Add("Display wajib ada.");
            return;
        }

        if (display.MaxTake is < 1 or > ModuleDisplay.HardMaxTake)
            errors.Add($"MaxTake harus antara 1 dan {ModuleDisplay.HardMaxTake}.");
        else if (display.Take < 1 || display.Take > display.MaxTake)
            errors.Add($"Take harus antara 1 dan MaxTake ({display.MaxTake}).");

        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in display.Columns ?? [])
        {
            if (string.IsNullOrWhiteSpace(column.Field))
            {
                errors.Add("Nama kolom (field) tidak boleh kosong.");
                continue;
            }
            var field = column.Field.Trim();
            if (field.Length > MaxFieldLength)
                errors.Add($"Nama kolom '{Shorten(field)}' lebih dari {MaxFieldLength} karakter.");
            else if (!fields.Add(field))
                errors.Add($"Kolom '{field}' dipakai lebih dari sekali.");

            if (column.Label is { Length: > MaxLabelLength })
                errors.Add($"Label kolom '{Shorten(field)}' maksimal {MaxLabelLength} karakter.");
            if (!Enum.IsDefined(column.Format))
                errors.Add($"Format kolom '{Shorten(field)}' tidak dikenal.");
        }
    }

    private static void ValidateRecordActions(ModuleConfig config, List<string> errors)
    {
        var source = config.Source;
        var sources = new[] { ("Detail", source?.Detail), ("Insert", source?.Insert), ("Update", source?.Update), ("Delete", source?.Delete) };
        foreach (var (name, reference) in sources)
        {
            if (reference is not null && reference.DataSourceId == Guid.Empty)
                errors.Add($"Data source untuk {name} belum dipilih.");
        }

        var keyField = config.Display?.KeyField?.Trim();
        if (keyField is { Length: > MaxFieldLength })
            errors.Add($"KeyField maksimal {MaxFieldLength} karakter.");
        else if (string.IsNullOrEmpty(keyField) && (source?.Detail is not null || source?.Update is not null || source?.Delete is not null))
            errors.Add("KeyField wajib diisi bila module punya Detail, Update, atau Delete.");

        if (source?.Update is not null && source.Detail is null)
            errors.Add("Update membutuhkan Detail: form ubah diisi dari data source Detail.");

        var fields = config.Form?.Fields ?? [];
        if (fields.Count > MaxFormFields)
            errors.Add($"Form maksimal {MaxFormFields} field.");
        if (fields.Count == 0 && (source?.Insert is not null || source?.Update is not null))
            errors.Add("Insert atau Update membutuhkan setidaknya satu field form.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Take(MaxFormFields))
            ValidateFormField(field, names, errors);
    }

    private static void ValidateFormField(ModuleFormField field, HashSet<string> names, List<string> errors)
    {
        var name = field.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            errors.Add("Nama field form tidak boleh kosong.");
            return;
        }
        if (name.Length > MaxFieldLength || !FieldNamePattern().IsMatch(name))
        {
            errors.Add($"Nama field form '{Shorten(name)}' harus huruf/angka/_ (tidak diawali angka), maksimal {MaxFieldLength} karakter.");
            return;
        }
        if (!names.Add(name))
        {
            errors.Add($"Field form '{name}' dipakai lebih dari sekali.");
            return;
        }

        if (field.Label is { Length: > MaxLabelLength })
            errors.Add($"Label field '{name}' maksimal {MaxLabelLength} karakter.");
        if (!Enum.IsDefined(field.Type))
        {
            errors.Add($"Tipe field '{name}' tidak dikenal.");
            return;
        }

        var isText = ModuleFieldValue.IsText(field.Type);
        if (field.MaxLength is { } max)
        {
            if (!isText)
                errors.Add($"MaxLength field '{name}' hanya untuk tipe text atau textarea.");
            else if (max < 1 || max > MaxFieldTextLength)
                errors.Add($"MaxLength field '{name}' harus antara 1 dan {MaxFieldTextLength}.");
        }

        if (!string.IsNullOrEmpty(field.Regex))
        {
            if (!isText)
                errors.Add($"Regex field '{name}' hanya untuk tipe text atau textarea.");
            else if (field.Regex.Length > MaxRegexLength)
                errors.Add($"Regex field '{name}' maksimal {MaxRegexLength} karakter.");
            else if (!IsCompilable(field.Regex))
                errors.Add($"Regex field '{name}' tidak valid.");
        }

        if (!string.IsNullOrEmpty(field.Default))
        {
            if (field.Default.Length > MaxFieldTextLength)
                errors.Add($"Default field '{name}' maksimal {MaxFieldTextLength} karakter.");
            else if (!ModuleFieldValue.IsValid(field.Type, field.Default))
                errors.Add($"Default field '{name}' tidak cocok dengan tipe {field.Type.ToString().ToLowerInvariant()}.");
        }
    }

    private static bool IsCompilable(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, ModuleFormField.RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the statement kind of the record action sources, which needs the data source rows: Detail must be a Query,
    /// Insert / Update / Delete must be Commands. <paramref name="kindOf"/> returns null for an unknown data source.
    /// The List source is checked by the caller (it has its own message).
    /// </summary>
    public static IReadOnlyList<string> ValidateRecordSourceKinds(ModuleSource? source, Func<Guid, StatementType?> kindOf)
    {
        var errors = new List<string>();
        Check("Detail", source?.Detail, StatementType.Query);
        Check("Insert", source?.Insert, StatementType.Command);
        Check("Update", source?.Update, StatementType.Command);
        Check("Delete", source?.Delete, StatementType.Command);
        return errors;

        void Check(string name, DataSourceRef? reference, StatementType expected)
        {
            if (reference is null || reference.DataSourceId == Guid.Empty) return;
            if (kindOf(reference.DataSourceId) is not { } actual)
                errors.Add($"Data source untuk {name} tidak ditemukan.");
            else if (actual != expected)
                errors.Add($"Data source untuk {name} harus berjenis {expected}.");
        }
    }

    private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "...";
}

/// <summary>
/// Structural validation of a menu item; hierarchy and target existence are checked by the repository.
/// A top-level item with neither module nor URL is a folder.
/// </summary>
public static partial class MenuInputValidator
{
    public const int MaxLabelLength = 100;
    public const int MaxIconLength = 50;
    public const int MaxUrlLength = 500;

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex IconPattern();

    public static IReadOnlyList<string> Validate(MenuInput input)
    {
        var errors = new List<string>();

        if (!input.IsDelimiter && string.IsNullOrWhiteSpace(input.Label))
            errors.Add("Label wajib diisi.");
        else if (input.Label is { } label && label.Trim().Length > MaxLabelLength)
            errors.Add($"Label maksimal {MaxLabelLength} karakter.");

        if (!string.IsNullOrWhiteSpace(input.Icon)
            && (input.Icon.Trim().Length > MaxIconLength || !IconPattern().IsMatch(input.Icon.Trim())))
            errors.Add($"Icon harus berupa nama (huruf/angka/_/-) maksimal {MaxIconLength} karakter.");

        var hasUrl = !string.IsNullOrWhiteSpace(input.Url);
        if (input.IsDelimiter)
        {
            if (input.ModuleId is not null || hasUrl)
                errors.Add("Delimiter tidak boleh menunjuk module atau URL.");
        }
        else if (input.ModuleId is not null && hasUrl)
        {
            errors.Add("Menu hanya boleh menunjuk satu: module atau URL.");
        }
        else if (input.ModuleId is null && !hasUrl && input.ParentId is not null)
        {
            // Without a target an item is a folder (a title for a dropdown); only top-level items can be folders.
            errors.Add("Item di dalam folder harus menunjuk module atau URL.");
        }
        else if (hasUrl && !IsSafeUrl(input.Url!.Trim()))
        {
            errors.Add($"URL harus diawali '/' (alamat di aplikasi ini) atau http:// / https://, maksimal {MaxUrlLength} karakter.");
        }

        return errors;
    }

    /// <summary>A path inside this app ("/object/page/..") or an absolute http(s) address; nothing like "javascript:".</summary>
    public static bool IsSafeUrl(string url)
    {
        if (url.Length > MaxUrlLength || url.Any(c => char.IsControl(c) || c == '\\')) return false;
        if (url.StartsWith('/')) return !url.StartsWith("//", StringComparison.Ordinal);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }
}
