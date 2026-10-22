using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Rakit.Core.DataSources;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

namespace Rakit.Core.Modules;

/// <summary>One line of the read-only record view. A section is a heading (<paramref name="Label"/>) with an optional description in <paramref name="Text"/>.</summary>
public sealed record ModuleRecordViewItem(string Label, string? Text, bool IsSection = false);

public interface IModuleRecordService
{
    /// <summary>
    /// The record with this key, loaded through the module's Detail data source (RouteValue <c>id</c> = key) and read from
    /// the first row. Values are keyed by form field name and written the way an input shows them (dates as yyyy-MM-dd,
    /// booleans as true/false, numbers with the invariant culture); a field without a matching column is null.
    /// </summary>
    /// <exception cref="ModuleNotFoundException">No such module, deleted, or still a draft.</exception>
    /// <exception cref="ModuleRecordUnavailableException"/>
    /// <exception cref="ModuleRecordValidationException">Blank or too long key, or a key that does not fit the data source.</exception>
    /// <exception cref="ModuleRecordNotFoundException">The data source returned no row.</exception>
    /// <exception cref="SqlExecutionException"/>
    Task<IReadOnlyDictionary<string, string?>> GetAsync(Guid moduleId, string key, ParameterContext context, CancellationToken ct = default);

    /// <summary>
    /// The record as read-only text for the "Lihat" modal (display.buttons.detail): the form's fields (section headings included)
    /// with their values formatted by type, or the Display columns when the module has no form. Same data source and errors as
    /// <see cref="GetAsync"/>; a module that does not offer the detail view is not configured for it (<c>NotConfigured</c>).
    /// </summary>
    Task<IReadOnlyList<ModuleRecordViewItem>> ViewAsync(Guid moduleId, string key, ParameterContext context, CancellationToken ct = default);

    /// <summary>
    /// Validates <paramref name="values"/> against the module's form and runs the Insert data source. A Table module whose
    /// Tambah button is switched off (display.buttons.add) does not offer it (<c>NotConfigured</c>), like Ubah and Hapus below.
    /// </summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleRecordUnavailableException"/>
    /// <exception cref="ModuleRecordValidationException">A field is invalid, or the database did not accept a value.</exception>
    /// <exception cref="ModuleRecordRejectedException">The procedure returned a non-zero code (rolled back).</exception>
    /// <exception cref="SqlExecutionException"/>
    Task InsertAsync(Guid moduleId, IReadOnlyDictionary<string, string?> values, ParameterContext context, CancellationToken ct = default);

    /// <summary>
    /// <see cref="InsertAsync"/> for one row of a CSV import: the import is offered by display.menu.import (checked by the import
    /// service), not by the Tambah button, so that button is not looked at here.
    /// </summary>
    Task InsertImportedAsync(Guid moduleId, IReadOnlyDictionary<string, string?> values, ParameterContext context, CancellationToken ct = default);

    /// <summary>Like <see cref="InsertAsync"/> for the Update data source, with RouteValue <c>id</c> = <paramref name="key"/>.</summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleRecordUnavailableException"/>
    /// <exception cref="ModuleRecordValidationException"/>
    /// <exception cref="ModuleRecordRejectedException"/>
    /// <exception cref="SqlExecutionException"/>
    Task UpdateAsync(Guid moduleId, string key, IReadOnlyDictionary<string, string?> values, ParameterContext context, CancellationToken ct = default);

    /// <summary>Runs the Delete data source with RouteValue <c>id</c> = <paramref name="key"/>.</summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleRecordUnavailableException"/>
    /// <exception cref="ModuleRecordValidationException"/>
    /// <exception cref="ModuleRecordRejectedException"/>
    /// <exception cref="SqlExecutionException"/>
    Task DeleteAsync(Guid moduleId, string key, ParameterContext context, CancellationToken ct = default);
}

/// <summary>
/// Record actions of a published Table module. Data reaches a data source only through FormField mappings (named after
/// configured, validated form fields) and the RouteValue <c>id</c> mapping; a request cannot add other form keys, query
/// strings or route values. Write calls run in a transaction that is committed only when the procedure returns 0.
/// </summary>
public sealed class ModuleRecordService(
    IModuleRepository modules, IDataSourceService dataSources, ILogger<ModuleRecordService> logger) : IModuleRecordService
{
    public const int MaxKeyLength = 200;

    private enum RecordAction { Detail, Insert, Update, Delete }

    public async Task<IReadOnlyDictionary<string, string?>> GetAsync(
        Guid moduleId, string key, ParameterContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var module = await GetPublishedAsync(moduleId, ct);
        var list = await LoadRecordAsync(module, key, context, ct);

        var row = list.Rows[0];
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in module.Config.Form.DataFields)
        {
            var name = field.Name.Trim();
            var index = IndexOf(list.Columns, name);
            values[name] = index >= 0 && index < row.Count ? FormatForInput(field.Type, row[index]) : null;
        }
        return values;
    }

    public async Task<IReadOnlyList<ModuleRecordViewItem>> ViewAsync(
        Guid moduleId, string key, ParameterContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var module = await GetPublishedAsync(moduleId, ct);
        if (!module.Config.Display.Buttons.Detail)
            throw new ModuleRecordUnavailableException("Aksi ini tidak tersedia untuk halaman ini.", "the module does not offer the detail view", notConfigured: true);

        var list = await LoadRecordAsync(module, key, context, ct);
        var row = list.Rows[0];
        object? Cell(string name) => IndexOf(list.Columns, name) is var i and >= 0 && i < row.Count ? row[i] : null;

        var items = new List<ModuleRecordViewItem>();
        var fields = module.Config.Form.Fields;
        if (fields.Any(f => !f.IsSection))
        {
            foreach (var field in fields)
            {
                var name = field.Name.Trim();
                var label = string.IsNullOrWhiteSpace(field.Label) ? name : field.Label.Trim();
                if (field.IsSection) { items.Add(new ModuleRecordViewItem(label, field.Help, IsSection: true)); continue; }

                var value = Cell(name);
                string? text;
                if (field.Type == FormFieldType.Select && field.Options is { Count: > 0 } options && ModuleFieldValue.ToText(value) is { } stored)
                    text = options.FirstOrDefault(o => string.Equals(o.Value?.Trim(), stored, StringComparison.Ordinal))?.Text is { Length: > 0 } t ? t : stored;
                else
                    text = ModuleCellFormatter.Format(value, ColumnFormatOf(field.Type));
                items.Add(new ModuleRecordViewItem(label, text));
            }
        }
        else
        {
            foreach (var column in module.Config.Display.Columns.Where(c => c.Visible))
            {
                var name = column.Field.Trim();
                items.Add(new ModuleRecordViewItem(string.IsNullOrWhiteSpace(column.Label) ? name : column.Label.Trim(), ModuleCellFormatter.Format(Cell(name), column.Format)));
            }
            // no configured columns: everything the Detail data source returned
            if (items.Count == 0)
                for (var i = 0; i < list.Columns.Count; i++)
                    items.Add(new ModuleRecordViewItem(list.Columns[i], ModuleCellFormatter.Plain(i < row.Count ? row[i] : null)));
        }
        return items;
    }

    private static ColumnFormat ColumnFormatOf(FormFieldType type) => type switch
    {
        FormFieldType.Number => ColumnFormat.Number,
        FormFieldType.Date => ColumnFormat.Date,
        FormFieldType.DateTime => ColumnFormat.DateTime,
        FormFieldType.Boolean => ColumnFormat.Boolean,
        _ => ColumnFormat.Text,
    };

    /// <summary>The Detail data source's result for a key, which has at least one row (otherwise <see cref="ModuleRecordNotFoundException"/>).</summary>
    private async Task<DataTableResult> LoadRecordAsync(Module module, string key, ParameterContext context, CancellationToken ct)
    {
        var dataSourceId = SourceOf(module, RecordAction.Detail);
        var normalizedKey = NormalizeKey(key);

        DataSourceResult result;
        try
        {
            result = await dataSources.ExecuteAsync(
                dataSourceId, Isolate(context, new Dictionary<string, string?>(), normalizedKey),
                new DataSourceRunRequest(0, 1, IncludeTotal: false), ct);
        }
        catch (Exception ex) when (IsSetupProblem(ex))
        {
            throw Unavailable(module, RecordAction.Detail, ex.GetType().Name + ": " + ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw Invalid("", ExceptionMessages.WithoutParameterSuffix(ex));
        }

        var list = result.List;
        if (list is null || list.Rows.Count == 0) throw new ModuleRecordNotFoundException(module.Id);
        return list;
    }

    public Task InsertAsync(
        Guid moduleId, IReadOnlyDictionary<string, string?> values, ParameterContext context, CancellationToken ct = default)
        => InsertAsync(moduleId, values, context, imported: false, ct);

    public Task InsertImportedAsync(
        Guid moduleId, IReadOnlyDictionary<string, string?> values, ParameterContext context, CancellationToken ct = default)
        => InsertAsync(moduleId, values, context, imported: true, ct);

    private async Task InsertAsync(
        Guid moduleId, IReadOnlyDictionary<string, string?> values, ParameterContext context, bool imported, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        var module = await GetPublishedAsync(moduleId, ct);
        if (!imported) EnsureButton(module, RecordAction.Insert);
        var dataSourceId = SourceOf(module, RecordAction.Insert);

        var form = ValidateFields(module, values, forInsert: true);
        await WriteAsync(module, RecordAction.Insert, dataSourceId, Isolate(context, form, null), ct);
    }

    public async Task UpdateAsync(
        Guid moduleId, string key, IReadOnlyDictionary<string, string?> values, ParameterContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(context);
        var module = await GetPublishedAsync(moduleId, ct);
        EnsureButton(module, RecordAction.Update);
        var dataSourceId = SourceOf(module, RecordAction.Update);
        var normalizedKey = NormalizeKey(key);

        var form = ValidateFields(module, values, forInsert: false);
        await WriteAsync(module, RecordAction.Update, dataSourceId, Isolate(context, form, normalizedKey), ct);
    }

    public async Task DeleteAsync(Guid moduleId, string key, ParameterContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var module = await GetPublishedAsync(moduleId, ct);
        EnsureButton(module, RecordAction.Delete);
        var dataSourceId = SourceOf(module, RecordAction.Delete);
        var normalizedKey = NormalizeKey(key);

        await WriteAsync(module, RecordAction.Delete, dataSourceId,
            Isolate(context, new Dictionary<string, string?>(), normalizedKey), ct);
    }

    // ---- running ---------------------------------------------------------------------------------

    private async Task WriteAsync(
        Module module, RecordAction action, Guid dataSourceId, ParameterContext context, CancellationToken ct)
    {
        DataSourceResult result;
        try
        {
            result = await dataSources.ExecuteAsync(
                dataSourceId, context, new DataSourceRunRequest(IncludeTotal: false, CommitOnlyIfReturnValueZero: true), ct);
        }
        catch (Exception ex) when (IsSetupProblem(ex))
        {
            throw Unavailable(module, action, ex.GetType().Name + ": " + ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            // A value the procedure parameter cannot take (wrong type, longer than the parameter).
            throw Invalid("", ExceptionMessages.WithoutParameterSuffix(ex));
        }

        if (result.ReturnValue is { } code and not 0)
        {
            logger.LogInformation("Module {ModuleId} {Action} was rejected by its procedure with code {Code}.", module.Id, action, code);
            throw new ModuleRecordRejectedException(code);
        }
    }

    private static bool IsSetupProblem(Exception ex) => ex is DataSourceNotFoundException or DataSourceForbiddenException
        or ParameterResolutionException or TableDefinitionException or SqlTemplateException or SchemaAccessDeniedException
        or SchemaObjectNotFoundException or ArgumentOutOfRangeException or InvalidOperationException;

    private async Task<Module> GetPublishedAsync(Guid moduleId, CancellationToken ct)
    {
        var module = await modules.GetAsync(moduleId, ct);
        if (module is null || module.Status != ModuleStatus.Published) throw new ModuleNotFoundException(moduleId);
        return module;
    }

    private Guid SourceOf(Module module, RecordAction action)
    {
        var source = module.Config.Source;
        var reference = action switch
        {
            RecordAction.Detail => source.Detail,
            RecordAction.Insert => source.Insert,
            RecordAction.Update => source.Update,
            _ => source.Delete,
        };
        if (reference is null || reference.DataSourceId == Guid.Empty)
            throw new ModuleRecordUnavailableException(
                "Aksi ini tidak tersedia untuk halaman ini.", $"the module has no {action} data source", notConfigured: true);
        return reference.DataSourceId;
    }

    /// <summary>
    /// A Table module's display.buttons switch Tambah / Ubah / Hapus off for the endpoints too, not only on the page (a button that
    /// is hidden must not still work for whoever posts to its URL). A Form module saves with its own button and has no such switch.
    /// </summary>
    private static void EnsureButton(Module module, RecordAction action)
    {
        if (module.Type != ModuleType.Table) return;
        var buttons = module.Config.Display.Buttons;
        var offered = action switch
        {
            RecordAction.Insert => buttons.Add,
            RecordAction.Update => buttons.Edit,
            RecordAction.Delete => buttons.Delete,
            _ => true,
        };
        if (!offered)
            throw new ModuleRecordUnavailableException(
                "Aksi ini tidak tersedia untuk halaman ini.", $"the module's {action} button is switched off", notConfigured: true);
    }

    private ModuleRecordUnavailableException Unavailable(Module module, RecordAction action, string reason, Exception inner)
    {
        logger.LogWarning(inner, "Module {ModuleId} ({ModuleName}) cannot run {Action}: {Reason}", module.Id, module.Name, action, reason);
        return new ModuleRecordUnavailableException("Aksi ini tidak tersedia saat ini.", reason, notConfigured: false, inner);
    }

    private static ModuleRecordValidationException Invalid(string field, string message)
        => new(new Dictionary<string, string> { [field] = message });

    /// <summary>
    /// A fresh context: only the validated form values and the record key. Query string, route values and form keys of
    /// the request are not passed on; claims and the clock are.
    /// </summary>
    private static ParameterContext Isolate(ParameterContext request, Dictionary<string, string?> form, string? key)
        => ModuleContexts.Isolate(request, form, key);

    private static string NormalizeKey(string? key)
    {
        var text = key?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxKeyLength)
            throw Invalid("", "Kunci data tidak valid.");
        return text;
    }

    // ---- field validation --------------------------------------------------------------------------

    /// <summary>
    /// Returns the form values to hand to the data source: configured, non-readonly fields that passed, plus (on insert)
    /// the default of a readonly field. A blank value is left out so the mapping's fallback applies, and a boolean that
    /// was not sent counts as false (an unchecked box is not submitted).
    /// </summary>
    internal static Dictionary<string, string?> ValidateFields(
        Module module, IReadOnlyDictionary<string, string?> submitted, bool forInsert)
        => ValidateFields(module.Config.Form.DataFields, submitted, forInsert);

    /// <summary>The same rules for any list of form fields (an Action step's form); sections in the list are skipped.</summary>
    internal static Dictionary<string, string?> ValidateFields(
        IEnumerable<ModuleFormField> fields, IReadOnlyDictionary<string, string?> submitted, bool forInsert)
    {
        var values = new Dictionary<string, string?>(submitted, StringComparer.OrdinalIgnoreCase);
        var form = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields.Where(f => !f.IsSection))
        {
            var name = field.Name.Trim();
            var label = string.IsNullOrWhiteSpace(field.Label) ? name : field.Label.Trim();

            if (field.Readonly)
            {
                if (forInsert && !string.IsNullOrEmpty(field.Default)) form[name] = field.Default;
                continue;
            }

            var text = values.GetValueOrDefault(name)?.Trim() ?? "";

            if (field.Type == FormFieldType.Boolean)
            {
                if (text.Length == 0) text = "false";
                if (!ModuleFieldValue.IsValid(field.Type, text)) errors[name] = $"{label} harus true atau false.";
                else form[name] = text.ToLowerInvariant();
                continue;
            }

            if (text.Length == 0)
            {
                if (field.Required) errors[name] = $"{label} wajib diisi.";
                continue;
            }

            if (ModuleFieldValue.IsText(field.Type))
            {
                if (field.Type is FormFieldType.Email or FormFieldType.Url && !ModuleFieldValue.IsValid(field.Type, text))
                {
                    errors[name] = field.Type == FormFieldType.Email
                        ? $"{label} bukan alamat email yang valid."
                        : $"{label} bukan alamat web (http atau https) yang valid.";
                    continue;
                }
                if (field.Type == FormFieldType.Select && field.Options is { Count: > 0 } options
                    && !options.Any(o => string.Equals(o.Value?.Trim(), text, StringComparison.Ordinal)))
                {
                    errors[name] = $"{label}: pilihan tidak valid.";
                    continue;
                }
                if (field.MaxLength is { } max && text.Length > max)
                {
                    errors[name] = $"{label} maksimal {max} karakter.";
                    continue;
                }
                if (!string.IsNullOrEmpty(field.Regex) && FailsRegex(field.Regex, text, out var unreadable))
                {
                    errors[name] = unreadable ? $"{label} tidak dapat diperiksa." : $"Format {label} tidak sesuai.";
                    continue;
                }
            }
            else if (!ModuleFieldValue.IsValid(field.Type, text))
            {
                errors[name] = field.Type switch
                {
                    FormFieldType.Number => $"{label} harus berupa angka.",
                    FormFieldType.Date => $"{label} bukan tanggal yang valid.",
                    _ => $"{label} bukan tanggal dan waktu yang valid.",
                };
                continue;
            }
            else if (RangeError(field, label, text) is { } rangeError)
            {
                errors[name] = rangeError;
                continue;
            }

            form[name] = text;
        }

        if (errors.Count > 0) throw new ModuleRecordValidationException(errors);
        return form;
    }

    /// <summary>Why a valid number / date is outside the field's Min / Max, or null when it is inside (or the field has none).</summary>
    private static string? RangeError(ModuleFormField field, string label, string text)
    {
        if (!ModuleFieldValue.HasRange(field.Type)) return null;
        var min = field.Min?.Trim();
        var max = field.Max?.Trim();
        if (!string.IsNullOrEmpty(min) && ModuleFieldValue.Compare(field.Type, text, min) is < 0) return $"{label} minimal {min}.";
        if (!string.IsNullOrEmpty(max) && ModuleFieldValue.Compare(field.Type, text, max) is > 0) return $"{label} maksimal {max}.";
        return null;
    }

    /// <summary>The whole value has to match the pattern; a pattern that runs past its time limit counts as a failure too.</summary>
    private static bool FailsRegex(string pattern, string text, out bool unreadable)
    {
        unreadable = false;
        try
        {
            return !Regex.IsMatch(text, $@"\A(?:{pattern})\z", RegexOptions.CultureInvariant, ModuleFormField.RegexTimeout);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
        {
            unreadable = true;
            return true;
        }
    }

    // ---- detail values -----------------------------------------------------------------------------

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string? FormatForInput(FormFieldType type, object? value)
    {
        var culture = CultureInfo.InvariantCulture;
        return value switch
        {
            null => null,
            bool b => b ? "true" : "false",
            DateTime d => FormatDate(type, d),
            DateTimeOffset o => FormatDate(type, o.DateTime),
            DateOnly d => d.ToString("yyyy-MM-dd", culture),
            IFormattable f => f.ToString(null, culture),
            _ => value.ToString(),
        };
    }

    private static string FormatDate(FormFieldType type, DateTime value)
    {
        var culture = CultureInfo.InvariantCulture;
        if (type == FormFieldType.Date) return value.ToString("yyyy-MM-dd", culture);
        return value.ToString(value.Second == 0 ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd'T'HH:mm:ss", culture);
    }
}
