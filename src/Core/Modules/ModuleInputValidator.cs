using System.Text.RegularExpressions;

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

    private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "...";
}

/// <summary>Structural validation of a menu item; hierarchy and target existence are checked by the repository.</summary>
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
        else if ((input.ModuleId is null) == !hasUrl) // neither given, or both given
        {
            errors.Add("Menu harus menunjuk tepat satu: module atau URL.");
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
