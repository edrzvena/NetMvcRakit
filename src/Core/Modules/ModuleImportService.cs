using System.Text;
using Rakit.Core.DataSources;
using Rakit.Core.Sql;

namespace Rakit.Core.Modules;

public sealed record ModuleImportFailure(int Row, string Message);

/// <param name="Total">Data rows read (blank lines do not count).</param>
/// <param name="Failures">At most <see cref="ModuleImportService.MaxReportedFailures"/>; <paramref name="Total"/> - <paramref name="Imported"/> is the real count.</param>
/// <param name="Notes">For example the columns of the file that were ignored.</param>
public sealed record ModuleImportResult(
    int Total, int Imported, IReadOnlyList<ModuleImportFailure> Failures, IReadOnlyList<string> Notes);

/// <summary>The file cannot be imported at all (empty, too big, wrong columns). The message is safe to show.</summary>
public sealed class ModuleImportException(string message) : Exception(message);

public interface IModuleImportService
{
    /// <summary>
    /// Inserts the rows of a CSV through the module's Insert data source, one row at a time with the form's own validation (so
    /// each row is its own transaction and a bad row does not stop the others). Needs <c>display.menu.import</c>.
    /// </summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleRecordUnavailableException">Not offered (<c>NotConfigured</c>), or the Insert data source cannot run.</exception>
    /// <exception cref="ModuleImportException">The file as a whole is not acceptable.</exception>
    Task<ModuleImportResult> ImportAsync(Guid moduleId, string csv, ParameterContext context, CancellationToken ct = default);

    /// <summary>A CSV with only the header row: the names of the fields a person can fill in.</summary>
    Task<string> TemplateAsync(Guid moduleId, CancellationToken ct = default);
}

public sealed class ModuleImportService(IModuleRuntimeService runtime, IModuleRecordService records) : IModuleImportService
{
    public const int MaxReportedFailures = 50;

    public async Task<string> TemplateAsync(Guid moduleId, CancellationToken ct = default)
    {
        var module = await ImportableAsync(moduleId, ct);
        return ModuleCsv.Write(Fillable(module).Select(f => f.Name.Trim()).ToList(), []);
    }

    public async Task<ModuleImportResult> ImportAsync(Guid moduleId, string csv, ParameterContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentNullException.ThrowIfNull(context);
        var module = await ImportableAsync(moduleId, ct);

        var table = ModuleCsv.Parse(csv, out _);
        if (table.Count == 0) throw new ModuleImportException("File kosong.");
        if (table.Count == 1) throw new ModuleImportException("File hanya berisi baris judul, tidak ada data.");
        if (table.Count - 1 > ModuleExport.MaxImportRows)
            throw new ModuleImportException($"Maksimal {ModuleExport.MaxImportRows} baris data per impor (file ini {table.Count - 1}). Bagi menjadi beberapa file.");

        // header → field (by name, then by label); two columns for one field is ambiguous, so refuse it
        var fillable = Fillable(module);
        var byHeader = new Dictionary<string, ModuleFormField>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fillable)
        {
            byHeader.TryAdd(field.Name.Trim(), field);
            if (!string.IsNullOrWhiteSpace(field.Label)) byHeader.TryAdd(field.Label.Trim(), field);
        }

        var header = table[0];
        var columnField = new ModuleFormField?[header.Count];
        var ignored = new List<string>();
        var seen = new HashSet<ModuleFormField>();
        for (var i = 0; i < header.Count; i++)
        {
            var title = ModuleCsv.UnguardFormula(header[i].Trim());
            if (title.Length == 0) continue;
            if (!byHeader.TryGetValue(title, out var field)) { ignored.Add(title); continue; }
            if (!seen.Add(field)) throw new ModuleImportException($"Kolom untuk field '{field.Name.Trim()}' muncul lebih dari sekali.");
            columnField[i] = field;
        }
        if (seen.Count == 0)
            throw new ModuleImportException("Tidak ada kolom yang cocok dengan field form. Pakai Template CSV (nama kolom = nama field).");

        var missing = fillable.Where(f => f.Required && !seen.Contains(f)).Select(f => f.Name.Trim()).ToList();
        if (missing.Count > 0)
            throw new ModuleImportException("Kolom wajib tidak ada di file: " + string.Join(", ", missing) + ".");

        var notes = new List<string>();
        if (ignored.Count > 0) notes.Add("Kolom yang tidak dikenal diabaikan: " + string.Join(", ", ignored) + ".");

        var failures = new List<ModuleImportFailure>();
        var imported = 0;
        for (var r = 1; r < table.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            var cells = table[r];
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columnField.Length && i < cells.Count; i++)
            {
                if (columnField[i] is not { } field) continue;
                var text = ModuleCsv.UnguardFormula(cells[i].Trim());
                if (text.Length == 0) continue;
                values[field.Name.Trim()] = field.Type == FormFieldType.Boolean ? NormalizeBoolean(text) : text;
            }

            try
            {
                await records.InsertImportedAsync(moduleId, values, context, ct);
                imported++;
            }
            catch (ModuleRecordValidationException ex)
            {
                Fail(failures, r + 1, string.Join(" ", ex.Errors.Values));
            }
            catch (ModuleRecordRejectedException ex)
            {
                Fail(failures, r + 1, ex.Message);
            }
            catch (SqlExecutionException ex)
            {
                Fail(failures, r + 1, ex.Kind == SqlExecErrorKind.Database ? "Gagal menyimpan baris ini." : ex.Message);
            }
        }

        return new ModuleImportResult(table.Count - 1, imported, failures, notes);
    }

    private static void Fail(List<ModuleImportFailure> failures, int row, string message)
    {
        if (failures.Count < MaxReportedFailures) failures.Add(new ModuleImportFailure(row, message));
    }

    /// <summary>ya / tidak, 1 / 0, y / n, yes / no, true / false (any case) → true / false; anything else is left for the validation to refuse.</summary>
    private static string NormalizeBoolean(string text) => text.ToLowerInvariant() switch
    {
        "1" or "y" or "ya" or "yes" or "true" => "true",
        "0" or "n" or "tidak" or "no" or "false" => "false",
        _ => text,
    };

    /// <summary>The fields a file can fill: data fields the client may send (not read-only).</summary>
    private static List<ModuleFormField> Fillable(Module module)
        => module.Config.Form.DataFields.Where(f => !f.Readonly).ToList();

    private async Task<Module> ImportableAsync(Guid moduleId, CancellationToken ct)
    {
        var module = await runtime.GetModuleAsync(moduleId, allowDraft: false, ct);
        if (module.Type != ModuleType.Table) throw new ModuleNotFoundException(moduleId);
        if (!module.Config.Display.Menu.Import || module.Config.Source.Insert is null || Fillable(module).Count == 0)
            throw new ModuleRecordUnavailableException("Aksi ini tidak tersedia untuk halaman ini.", "the module does not offer import", notConfigured: true);
        return module;
    }

    /// <summary>The bytes of a CSV text with the UTF-8 byte order mark.</summary>
    public static byte[] WithBom(string csv) => new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
}
