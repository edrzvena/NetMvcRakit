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
    public const int MaxLookupFieldLength = 128;

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

        switch (input.Type)
        {
            case ModuleType.Table:
                if (config.Source?.List is null || config.Source.List.DataSourceId == Guid.Empty)
                    errors.Add("Module Table membutuhkan data source untuk list.");
                ValidateDisplay(config.Display, errors);
                ValidateRecordActions(config, errors);
                break;
            case ModuleType.Form:
                ValidateFormModule(config, errors);
                break;
            case ModuleType.Page:
                ValidatePageModule(config, errors);
                break;
            case ModuleType.Chart:
                ValidateListOnly(config, "Chart", errors);
                ValidateChart(config.Chart, errors);
                break;
            case ModuleType.Widget:
                ValidateListOnly(config, "Widget", errors);
                ValidateWidget(config.Widget, errors);
                break;
        }

        ValidateActions(config, input.Type, errors);

        if (config.Access?.View is { } view && view.Any(r => string.IsNullOrWhiteSpace(r) || r.Trim().Length > MaxRoleLength))
            errors.Add($"Akses view berisi entri kosong atau lebih dari {MaxRoleLength} karakter.");

        return errors;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,49}$")]
    private static partial Regex ActionCodePattern();

    /// <summary>Workflow buttons: Table and Form modules only. Whether the workflow exists is checked by the builder screen.</summary>
    private static void ValidateActions(ModuleConfig config, ModuleType type, List<string> errors)
    {
        var actions = config.Actions ?? [];
        if (actions.Count == 0) return;

        if (type is not (ModuleType.Table or ModuleType.Form))
        {
            errors.Add($"Module {type} tidak punya tombol aksi workflow (hanya Table dan Form).");
            return;
        }
        if (actions.Count > ModuleAction.MaxPerModule)
            errors.Add($"Satu module maksimal {ModuleAction.MaxPerModule} aksi.");

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var code = action.Code?.Trim() ?? "";
            var label = $"Aksi {i + 1}" + (code.Length > 0 ? $" ('{code}')" : "");

            if (!ActionCodePattern().IsMatch(code))
                errors.Add($"{label}: kode harus 1-{ModuleAction.MaxCodeLength} karakter, huruf/angka/'_'/'-', diawali huruf.");
            else if (!codes.Add(code))
                errors.Add($"{label}: kode dipakai lebih dari sekali.");

            if (string.IsNullOrWhiteSpace(action.Label))
                errors.Add($"{label}: label wajib diisi.");
            else if (action.Label.Trim().Length > ModuleAction.MaxLabelLength)
                errors.Add($"{label}: label maksimal {ModuleAction.MaxLabelLength} karakter.");

            if (action.WorkflowId == Guid.Empty)
                errors.Add($"{label}: workflow belum dipilih.");
            if (!string.IsNullOrWhiteSpace(action.Style) && !ModuleAction.Styles.Contains(action.Style.Trim(), StringComparer.OrdinalIgnoreCase))
                errors.Add($"{label}: warna tombol harus salah satu dari {string.Join(", ", ModuleAction.Styles)}.");
            if (action.Confirm is { Length: > ModuleAction.MaxConfirmLength })
                errors.Add($"{label}: teks konfirmasi maksimal {ModuleAction.MaxConfirmLength} karakter.");

            if (action.PerRow)
            {
                if (type != ModuleType.Table)
                    errors.Add($"{label}: tombol per baris hanya untuk module Table.");
                else if (string.IsNullOrWhiteSpace(config.Display?.KeyField))
                    errors.Add($"{label}: tombol per baris membutuhkan KeyField di tab Display.");
            }
        }
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

        if (display.Title is { Length: > ModuleDisplay.MaxTitleLength })
            errors.Add($"Judul halaman maksimal {ModuleDisplay.MaxTitleLength} karakter.");
        if (display.SearchPlaceholder is { Length: > ModuleDisplay.MaxSearchPlaceholderLength })
            errors.Add($"Placeholder pencarian maksimal {ModuleDisplay.MaxSearchPlaceholderLength} karakter.");
        ValidateFilters(display.Filters ?? [], errors);
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex FilterNamePattern();

    /// <summary>The filter panel: unique query-string names, a known type, and for a select its choices (lookup or fixed options).</summary>
    private static void ValidateFilters(IReadOnlyList<ModuleFilter> filters, List<string> errors)
    {
        if (filters.Count > ModuleFilter.MaxPerModule)
            errors.Add($"Satu module maksimal {ModuleFilter.MaxPerModule} filter.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < filters.Count && i < ModuleFilter.MaxPerModule; i++)
        {
            var filter = filters[i];
            var name = filter.Name?.Trim() ?? "";
            var title = $"Filter {i + 1}" + (name.Length > 0 ? $" ('{Shorten(name)}')" : "");

            if (!FilterNamePattern().IsMatch(name))
                errors.Add($"{title}: nama harus huruf/angka/_ (diawali huruf), maksimal {ModuleFilter.MaxNameLength} karakter.");
            else if (!names.Add(name))
                errors.Add($"{title}: nama dipakai lebih dari sekali.");

            if (filter.Label is { Length: > ModuleFilter.MaxLabelLength })
                errors.Add($"{title}: label maksimal {ModuleFilter.MaxLabelLength} karakter.");
            if (filter.Placeholder is { Length: > ModuleFormField.MaxPlaceholderLength })
                errors.Add($"{title}: placeholder maksimal {ModuleFormField.MaxPlaceholderLength} karakter.");
            if (!ModuleFilter.Types.Contains(filter.Type))
            {
                errors.Add($"{title}: tipe harus salah satu dari text, number, date, dateTime, boolean, select.");
                continue;
            }

            var hasOptions = filter.Options is { Count: > 0 };
            if (filter.Type != FormFieldType.Select)
            {
                if (filter.Lookup is not null || hasOptions) errors.Add($"{title}: pilihan hanya untuk tipe select.");
            }
            else if (hasOptions && filter.Lookup is not null)
            {
                errors.Add($"{title}: pilih salah satu, lookup atau opsi tetap.");
            }
            else if (hasOptions)
            {
                CheckOptions(filter.Options, title, errors);
            }
            else
            {
                CheckLookup(filter.Lookup, title, errors);
            }
        }
    }

    /// <summary>Fixed choices: at most <see cref="ModuleFormField.MaxOptions"/>, each with a distinct non-blank value.</summary>
    private static void CheckOptions(IReadOnlyList<ModuleOption>? options, string title, List<string> errors)
    {
        if (options is null || options.Count == 0)
        {
            errors.Add($"{title}: opsi tetap belum diisi.");
            return;
        }
        if (options.Count > ModuleFormField.MaxOptions)
            errors.Add($"{title}: maksimal {ModuleFormField.MaxOptions} opsi.");

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options.Take(ModuleFormField.MaxOptions))
        {
            var value = option.Value?.Trim() ?? "";
            if (value.Length == 0) errors.Add($"{title}: nilai opsi tidak boleh kosong.");
            else if (value.Length > MaxFieldTextLength) errors.Add($"{title}: nilai opsi maksimal {MaxFieldTextLength} karakter.");
            else if (!values.Add(value)) errors.Add($"{title}: nilai opsi '{Shorten(value)}' dipakai lebih dari sekali.");
            if (option.Text is { Length: > MaxLabelLength }) errors.Add($"{title}: teks opsi maksimal {MaxLabelLength} karakter.");
        }
    }

    /// <summary>A lookup needs a data source and both column names.</summary>
    private static void CheckLookup(ModuleLookup? lookup, string title, List<string> errors)
    {
        if (lookup is null || lookup.DataSourceId == Guid.Empty)
        {
            errors.Add($"{title}: membutuhkan lookup dengan data source (atau opsi tetap).");
            return;
        }
        foreach (var (what, value) in new[] { ("ValueField", lookup.ValueField), ("TextField", lookup.TextField) })
        {
            var text = value?.Trim() ?? "";
            if (text.Length == 0) errors.Add($"{title}: {what} lookup wajib diisi.");
            else if (text.Length > MaxLookupFieldLength) errors.Add($"{title}: {what} lookup maksimal {MaxLookupFieldLength} karakter.");
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

        var hasDataFields = config.Form?.DataFields.Any() ?? false;
        if (!hasDataFields && (source?.Insert is not null || source?.Update is not null))
            errors.Add("Insert atau Update membutuhkan setidaknya satu field form.");
        ValidateFormFields(config, errors);

        if (config.Display?.Buttons.Detail == true && (source?.Detail is null || string.IsNullOrEmpty(keyField)))
            errors.Add("Tombol Detail membutuhkan data source Detail dan kolom kunci.");
        if (config.Display?.Menu.Import == true && (source?.Insert is null || !hasDataFields))
            errors.Add("Menu Import membutuhkan data source Insert dan setidaknya satu field form.");
    }

    private static void ValidateFormFields(ModuleConfig config, List<string> errors)
    {
        var fields = config.Form?.Fields ?? [];
        if (fields.Count > MaxFormFields)
            errors.Add($"Form maksimal {MaxFormFields} field.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Take(MaxFormFields))
            ValidateFormField(field, names, errors);
    }

    /// <summary>
    /// The field rules of a module form for a list of fields that live elsewhere (an Action step). The caller limits the count.
    /// A select needs fixed options (nothing here can read a lookup data source) and a browse field is not available.
    /// </summary>
    public static IReadOnlyList<string> ValidateStandaloneFields(IReadOnlyList<ModuleFormField> fields)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            ValidateFormField(field, names, errors);
            var name = field.Name?.Trim() ?? "";
            if (field.Type == FormFieldType.Browse) errors.Add($"Field '{name}': tipe browse tidak tersedia di langkah Form.");
            else if (field.Type == FormFieldType.Select && field.Lookup is not null)
                errors.Add($"Field '{name}': select di langkah Form memakai opsi tetap, bukan lookup.");
        }
        return errors;
    }

    /// <summary>A Form module only inserts: an Insert (Command) source and at least one field; no list, detail, update or delete.</summary>
    private static void ValidateFormModule(ModuleConfig config, List<string> errors)
    {
        var source = config.Source;
        if (source?.Insert is null || source.Insert.DataSourceId == Guid.Empty)
            errors.Add("Module Form membutuhkan data source Insert.");
        if (source?.List is not null || source?.Detail is not null || source?.Update is not null || source?.Delete is not null)
            errors.Add("Module Form hanya memakai data source Insert.");
        if (!(config.Form?.DataFields.Any() ?? false))
            errors.Add("Module Form membutuhkan setidaknya satu field form.");
        ValidateFormFields(config, errors);
    }

    /// <summary>Chart and Widget modules read one Query data source (List) and nothing else.</summary>
    private static void ValidateListOnly(ModuleConfig config, string typeName, List<string> errors)
    {
        var source = config.Source;
        if (source?.List is null || source.List.DataSourceId == Guid.Empty)
            errors.Add($"Module {typeName} membutuhkan data source untuk datanya (List).");
        if (source?.Detail is not null || source?.Insert is not null || source?.Update is not null || source?.Delete is not null)
            errors.Add($"Module {typeName} hanya memakai data source List.");
    }

    private static void ValidateField(string? value, string title, List<string> errors, bool required = true)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0) { if (required) errors.Add($"{title} wajib diisi."); }
        else if (text.Length > ModuleWidget.MaxFieldLength) errors.Add($"{title} maksimal {ModuleWidget.MaxFieldLength} karakter.");
    }

    private static void ValidateChart(ModuleChart? chart, List<string> errors)
    {
        if (chart is null) { errors.Add("Config chart wajib ada."); return; }

        var kindOk = Enum.IsDefined(chart.Kind);
        if (!kindOk) errors.Add("Jenis chart tidak dikenal.");
        ValidateField(chart.LabelField, "Kolom label chart", errors);

        var series = chart.Series ?? [];
        if (series.Count == 0) errors.Add("Chart membutuhkan setidaknya satu seri.");
        else if (series.Count > ModuleChart.MaxSeries) errors.Add($"Chart maksimal {ModuleChart.MaxSeries} seri.");
        else if (kindOk && chart.Kind is ChartKind.Pie or ChartKind.Doughnut && series.Count != 1)
            errors.Add("Chart pie dan doughnut memakai tepat satu seri.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < series.Count && i < ModuleChart.MaxSeries; i++)
        {
            var s = series[i];
            var n = i + 1;
            var column = s.ValueField?.Trim() ?? "";
            if (column.Length == 0) errors.Add($"Seri {n}: kolom nilai wajib diisi.");
            else if (column.Length > ModuleWidget.MaxFieldLength) errors.Add($"Seri {n}: kolom nilai maksimal {ModuleWidget.MaxFieldLength} karakter.");
            else if (!seen.Add(column)) errors.Add($"Seri {n}: kolom '{column}' dipakai lebih dari sekali.");
            if (s.Label is { Length: > ModuleWidget.MaxLabelLength }) errors.Add($"Seri {n}: label maksimal {ModuleWidget.MaxLabelLength} karakter.");
            if (!string.IsNullOrWhiteSpace(s.Color) && !ColorPattern().IsMatch(s.Color.Trim()))
                errors.Add($"Seri {n}: warna harus berbentuk #RRGGBB.");
        }

        if (chart.Stacked && kindOk && chart.Kind is ChartKind.Pie or ChartKind.Doughnut)
            errors.Add("Stacked hanya untuk chart bar, line, atau area.");
        if (chart.Horizontal && kindOk && chart.Kind != ChartKind.Bar)
            errors.Add("Horizontal hanya untuk chart bar.");
        if (chart.Title is { Length: > ModuleChart.MaxTitleLength }) errors.Add($"Judul chart maksimal {ModuleChart.MaxTitleLength} karakter.");
        if (chart.MaxRows is < 1 or > ModuleChart.HardMaxRows) errors.Add($"MaxRows harus antara 1 dan {ModuleChart.HardMaxRows}.");
    }

    private static void ValidateWidget(ModuleWidget? widget, List<string> errors)
    {
        if (widget is null) { errors.Add("Config widget wajib ada."); return; }

        ValidateField(widget.ValueField, "Kolom nilai widget", errors);
        ValidateField(widget.LabelField, "Kolom label widget", errors, required: false);
        if (widget.Label is { Length: > ModuleWidget.MaxLabelLength }) errors.Add($"Label widget maksimal {ModuleWidget.MaxLabelLength} karakter.");
        if (!Enum.IsDefined(widget.Format)) errors.Add("Format widget tidak dikenal.");
        if (widget.Decimals is < 0 or > ModuleWidget.MaxDecimals) errors.Add($"Desimal harus antara 0 dan {ModuleWidget.MaxDecimals}.");
        if (widget.Prefix is { Length: > ModuleWidget.MaxAffixLength }) errors.Add($"Awalan maksimal {ModuleWidget.MaxAffixLength} karakter.");
        if (widget.Suffix is { Length: > ModuleWidget.MaxAffixLength }) errors.Add($"Akhiran maksimal {ModuleWidget.MaxAffixLength} karakter.");
        if (!ModuleWidget.Colors.Contains(widget.Color?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
            errors.Add($"Warna widget harus salah satu dari: {string.Join(", ", ModuleWidget.Colors)}.");
        if (widget.RefreshSeconds != 0 && (widget.RefreshSeconds < ModuleWidget.MinRefreshSeconds || widget.RefreshSeconds > ModuleWidget.MaxRefreshSeconds))
            errors.Add($"Refresh harus 0 (mati) atau {ModuleWidget.MinRefreshSeconds}-{ModuleWidget.MaxRefreshSeconds} detik.");
    }

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    /// <summary>A Page module is HTML only (embedded modules are checked against the database by the builder).</summary>
    private static void ValidatePageModule(ModuleConfig config, List<string> errors)
    {
        var source = config.Source;
        if (source?.List is not null || source?.Detail is not null || source?.Insert is not null
            || source?.Update is not null || source?.Delete is not null)
            errors.Add("Module Page tidak memakai data source.");

        var html = config.Page?.Html;
        if (string.IsNullOrWhiteSpace(html))
            errors.Add("Module Page membutuhkan isi HTML.");
        else if (html.Length > ModulePage.MaxHtmlLength)
            errors.Add($"Isi HTML maksimal {ModulePage.MaxHtmlLength} karakter.");
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

        if (field.Placeholder is { Length: > ModuleFormField.MaxPlaceholderLength })
            errors.Add($"Placeholder field '{name}' maksimal {ModuleFormField.MaxPlaceholderLength} karakter.");
        if (field.Help is { Length: > ModuleFormField.MaxHelpLength })
            errors.Add($"Teks bantuan field '{name}' maksimal {ModuleFormField.MaxHelpLength} karakter.");
        if (field.ColSize is < 1 or > 12)
            errors.Add($"Ukuran kolom field '{name}' harus antara 1 dan 12.");

        // A section is a heading: it needs a caption and holds no value, so nothing else about it is judged.
        if (field.IsSection)
        {
            if (string.IsNullOrWhiteSpace(field.Label)) errors.Add($"Judul seksi '{name}' wajib diisi (label).");
            return;
        }

        ValidateLookup(field, name, errors);
        ValidateRange(field, name, errors);

        var isText = ModuleFieldValue.IsText(field.Type);
        if (field.MaxLength is { } max)
        {
            if (!isText)
                errors.Add($"MaxLength field '{name}' hanya untuk tipe text, textarea, select, browse, email, atau url.");
            else if (max < 1 || max > MaxFieldTextLength)
                errors.Add($"MaxLength field '{name}' harus antara 1 dan {MaxFieldTextLength}.");
        }

        if (!string.IsNullOrEmpty(field.Regex))
        {
            if (!isText)
                errors.Add($"Regex field '{name}' hanya untuk tipe text, textarea, select, browse, email, atau url.");
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
            else if (field.Type == FormFieldType.Select && field.Options is { Count: > 0 }
                     && field.Options.All(o => !string.Equals(o.Value?.Trim(), field.Default, StringComparison.Ordinal)))
                errors.Add($"Default field '{name}' harus salah satu opsi.");
        }
    }

    /// <summary>Min / Max (number, date, dateTime) and Step (number): written as they would be typed, and consistent.</summary>
    private static void ValidateRange(ModuleFormField field, string name, List<string> errors)
    {
        var min = field.Min?.Trim() ?? "";
        var max = field.Max?.Trim() ?? "";
        if (!ModuleFieldValue.HasRange(field.Type))
        {
            if (min.Length > 0 || max.Length > 0) errors.Add($"Min dan Max field '{name}' hanya untuk tipe number, date, atau dateTime.");
        }
        else
        {
            if (min.Length > 0 && !ModuleFieldValue.IsValid(field.Type, min))
                errors.Add($"Min field '{name}' tidak cocok dengan tipe {field.Type.ToString().ToLowerInvariant()}.");
            if (max.Length > 0 && !ModuleFieldValue.IsValid(field.Type, max))
                errors.Add($"Max field '{name}' tidak cocok dengan tipe {field.Type.ToString().ToLowerInvariant()}.");
            if (min.Length > 0 && max.Length > 0 && ModuleFieldValue.Compare(field.Type, min, max) is > 0)
                errors.Add($"Min field '{name}' tidak boleh lebih besar dari Max.");
        }

        var step = field.Step?.Trim() ?? "";
        if (step.Length > 0)
        {
            if (field.Type != FormFieldType.Number)
                errors.Add($"Step field '{name}' hanya untuk tipe number.");
            else if (!decimal.TryParse(step, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) || s <= 0)
                errors.Add($"Step field '{name}' harus angka lebih besar dari 0.");
        }
    }

    private static void ValidateLookup(ModuleFormField field, string name, List<string> errors)
    {
        var title = $"Field '{name}'";
        var hasOptions = field.Options is { Count: > 0 };

        if (field.Type != FormFieldType.Select && hasOptions)
            errors.Add($"Opsi field '{name}' hanya untuk tipe select.");

        if (!ModuleFieldValue.IsLookup(field.Type))
        {
            if (field.Lookup is not null)
                errors.Add($"Lookup field '{name}' hanya untuk tipe select atau browse.");
            return;
        }

        if (field.Type == FormFieldType.Select && hasOptions)
        {
            if (field.Lookup is not null) errors.Add($"{title}: pilih salah satu, lookup atau opsi tetap.");
            else CheckOptions(field.Options, title, errors);
            return;
        }

        CheckLookup(field.Lookup, title, errors);
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

    /// <summary>
    /// Checks the lookup data sources of select / browse fields, which needs the data source rows: each must exist and be a
    /// Query (a lookup is read on a GET and must never write). <paramref name="kindOf"/> returns null for an unknown data source.
    /// </summary>
    public static IReadOnlyList<string> ValidateLookupSourceKinds(IEnumerable<ModuleFormField> fields, Func<Guid, StatementType?> kindOf)
    {
        var errors = new List<string>();
        foreach (var field in fields)
        {
            if (!ModuleFieldValue.IsLookup(field.Type) || field.Lookup is null || field.Lookup.DataSourceId == Guid.Empty) continue;
            var name = field.Name?.Trim() ?? "";
            if (kindOf(field.Lookup.DataSourceId) is not { } kind)
                errors.Add($"Data source lookup untuk field '{name}' tidak ditemukan.");
            else if (kind != StatementType.Query)
                errors.Add($"Data source lookup untuk field '{name}' harus berjenis Query.");
        }
        return errors;
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
