using System.Text;
using Rakit.Core.DataSources;

namespace Rakit.Core.Modules;

/// <param name="Content">UTF-8 with a byte order mark, so Excel opens it with the right encoding.</param>
/// <param name="Truncated">More rows matched than <see cref="ModuleExport.MaxRows"/>; the file holds the first ones.</param>
public sealed record ModuleExportResult(string FileName, byte[] Content, int Rows, bool Truncated);

public interface IModuleExportService
{
    /// <summary>
    /// A CSV of a published Table module's list: the rows of the requested page (<paramref name="all"/> = false, needs
    /// <c>display.menu.export</c>) or every row that matches the request's search and sort, read in chunks up to
    /// <see cref="ModuleExport.MaxRows"/> rows (<c>display.menu.exportAll</c>). The columns are the visible Display columns with
    /// their labels. Filters travel in <paramref name="context"/> like on the page.
    /// </summary>
    /// <exception cref="ModuleNotFoundException"/>
    /// <exception cref="ModuleRecordUnavailableException">The module does not offer this export (<c>NotConfigured</c>).</exception>
    /// <exception cref="ModuleListUnavailableException"/>
    /// <exception cref="ModuleListRequestException"/>
    /// <exception cref="Sql.SqlExecutionException"/>
    Task<ModuleExportResult> ExportAsync(
        Guid moduleId, ParameterContext context, ModuleListRequest request, bool all, CancellationToken ct = default);
}

public sealed class ModuleExportService(IModuleRuntimeService runtime) : IModuleExportService
{
    public async Task<ModuleExportResult> ExportAsync(
        Guid moduleId, ParameterContext context, ModuleListRequest request, bool all, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var module = await runtime.GetModuleAsync(moduleId, allowDraft: false, ct);
        if (module.Type != ModuleType.Table) throw new ModuleNotFoundException(moduleId);
        var menu = module.Config.Display.Menu;
        if (all ? !menu.ExportAll : !menu.Export)
            throw new ModuleRecordUnavailableException("Aksi ini tidak tersedia untuk halaman ini.", "the module does not offer this export", notConfigured: true);

        IReadOnlyList<ModuleListColumn> columns;
        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;

        if (!all)
        {
            var page = await runtime.GetListAsync(module, context, request with { Export = true }, ct);
            columns = page.Columns;
            rows.AddRange(page.Rows);
            truncated = page.Truncated && !page.HasNext;
        }
        else
        {
            var skip = 0;
            ModuleListResult? chunk;
            columns = [];
            do
            {
                chunk = await runtime.GetListAsync(
                    module, context, new ModuleListRequest(skip, ModuleExport.ChunkRows, request.OrderBy, request.Search, Export: true), ct);
                columns = chunk.Columns;
                rows.AddRange(chunk.Rows);
                skip += chunk.Rows.Count;
                if (rows.Count >= ModuleExport.MaxRows)
                {
                    truncated = chunk.HasNext || rows.Count > ModuleExport.MaxRows || chunk.Truncated;
                    rows.RemoveRange(ModuleExport.MaxRows, rows.Count - ModuleExport.MaxRows);
                    break;
                }
            }
            while (chunk.HasNext && chunk.Rows.Count > 0);
            // a data source that does not page stops at the rows one call can read
            truncated |= chunk.Truncated && !chunk.HasNext;
        }

        var csv = ModuleCsv.Write(
            columns.Select(c => ModuleCsv.GuardFormula(c.Label)).ToList(),
            rows.Select(r => (IReadOnlyList<string?>)r.Select((value, i) => ModuleCsv.Cell(value, columns[i].Format)).ToList()));

        var content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return new ModuleExportResult(FileNameFor(module.Name, DateTime.Now), content, rows.Count, truncated);
    }

    /// <summary><c>Nama-Module-20261020-153000.csv</c>: letters and digits of the name, anything else becomes '-'.</summary>
    public static string FileNameFor(string moduleName, DateTime now)
    {
        var sb = new StringBuilder();
        foreach (var c in moduleName.Trim()) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var stem = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (stem.Length == 0) stem = "data";
        if (stem.Length > 60) stem = stem[..60].TrimEnd('-');
        return $"{stem}-{now:yyyyMMdd-HHmmss}.csv";
    }
}
