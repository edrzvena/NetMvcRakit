using System.Text.Json;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.DataSources;
using Rakit.Core.Playground;
using Rakit.Core.Procedures;
using Rakit.Core.Schema;

namespace Rakit.Core.Modules;

/// <summary>
/// The "Buat dari tabel" wizard of the module builder. <see cref="InspectAsync"/> describes a table for the wizard;
/// <see cref="ApplyAsync"/> makes the data sources (and the CRUD procedures behind Insert / Update / Delete) and suggests the
/// Display and Form settings. Nothing here saves a module: the builder keeps the result in the form until the module is saved.
/// </summary>
public interface ITableModuleGenerator
{
    /// <summary>True when the wizard may create procedures (Rakit:Playground:Enabled and AllowWrite).</summary>
    bool CanCreateProcedures { get; }

    /// <exception cref="SchemaAccessDeniedException">Schema is not allowed.</exception>
    /// <exception cref="SchemaObjectNotFoundException">No such table or view.</exception>
    /// <exception cref="ArgumentException">The object is not a table or view.</exception>
    Task<TableInspection> InspectAsync(string schema, string name, CancellationToken ct = default);

    /// <exception cref="TableModuleException">The request cannot be done (see its kind).</exception>
    Task<TableModuleResult> ApplyAsync(TableModuleRequest request, string user, CancellationToken ct = default);
}

public sealed class TableModuleGenerator(
    IObjectCatalog catalog,
    IPlaygroundService playground,
    ISchemaBrowserService schemaBrowser,
    ISpWhitelistService whitelist,
    IDataSourceRepository dataSources,
    IOptions<RakitDbOptions> dbOptions) : ITableModuleGenerator
{
    private string[] Schemas => dbOptions.Value.AllowedSchemas;

    public bool CanCreateProcedures => playground.Settings is { Enabled: true, AllowWrite: true };

    // ---- inspect -------------------------------------------------------------------------------

    public async Task<TableInspection> InspectAsync(string schema, string name, CancellationToken ct = default)
        => Describe(await ReadTableAsync(schema, name, ct));

    private async Task<PlaygroundObjectDetail> ReadTableAsync(string schema, string name, CancellationToken ct)
    {
        var detail = await catalog.GetObjectAsync(schema, name, ct);
        if (detail.Kind is not ("TABLE" or "VIEW"))
            throw new ArgumentException("Hanya tabel dan view yang bisa dipakai.");
        return detail;
    }

    private TableInspection Describe(PlaygroundObjectDetail detail)
    {
        var notes = new List<string>();
        var columns = detail.Columns.Select(Choice).ToList();

        var keys = detail.Columns.Where(c => c.IsPrimaryKey).ToList();
        var key = keys.Count == 1 ? keys[0].Name : null;
        var composite = keys.Count > 1;
        var isTable = detail.Kind == "TABLE";

        if (!isTable) notes.Add("View hanya bisa dibaca: tidak ada aksi Tambah, Ubah, atau Hapus.");
        else if (composite) notes.Add("Tabel ini punya kunci komposit: hanya List yang dibuat (Detail, Ubah, Hapus butuh satu kolom kunci).");
        else if (key is null) notes.Add("Tabel ini tidak punya primary key: hanya List dan Tambah yang bisa dibuat. Pilih kolom kunci sendiri bila ingin Detail, Ubah, atau Hapus.");

        return new TableInspection(detail.Schema, detail.Name, detail.Kind, columns, key, composite, isTable, CanCreateProcedures, notes);
    }

    private static TableColumnChoice Choice(PlaygroundColumn c)
    {
        var baseType = TableModuleMapping.BaseType(c.Type);
        var listable = TableModuleMapping.IsListable(baseType);
        var computed = c.ComputedDefinition is not null;
        var writable = listable && TableModuleMapping.IsWritableType(baseType) && !c.IsIdentity && !computed
                       && TableModuleMapping.IsPlainName(c.Name);

        string? note = null;
        if (!listable) note = "tipe tidak didukung";
        else if (c.IsIdentity) note = "identity: diisi database";
        else if (computed) note = "kolom hitung: diisi database";
        else if (!TableModuleMapping.IsWritableType(baseType)) note = "tipe lama: hanya untuk dibaca";
        else if (!TableModuleMapping.IsPlainName(c.Name)) note = "nama kolom tidak didukung untuk aksi tulis";

        return new TableColumnChoice(
            c.Name, c.Type, c.IsNullable, c.IsIdentity, c.IsPrimaryKey, c.DefaultDefinition is not null,
            listable, writable, note,
            CamelCase(TableModuleMapping.Format(baseType)), CamelCase(TableModuleMapping.FieldType(c.Type)));
    }

    private static string CamelCase<T>(T value) where T : struct, Enum
        => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    // ---- apply ---------------------------------------------------------------------------------

    private sealed record Role(string Key, string Operation, string Label);

    private static readonly Role InsertRole = new("insert", "Insert", "Tambah");
    private static readonly Role UpdateRole = new("update", "Update", "Ubah");
    private static readonly Role DeleteRole = new("delete", "Delete", "Hapus");

    public async Task<TableModuleResult> ApplyAsync(TableModuleRequest request, string user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var detail = await ReadTableAsync(request.Schema, request.Name, ct);
        var byName = detail.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var inspection = Describe(detail);
        var notes = new List<string>();

        // ---- what was asked, checked ----
        var selected = new List<PlaygroundColumn>();
        foreach (var name in request.Columns.Select(c => c?.Trim() ?? "").Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byName.TryGetValue(name, out var column))
                throw Invalid($"Kolom '{name}' tidak ada di {detail.Schema}.{detail.Name}.");
            if (!TableModuleMapping.IsListable(TableModuleMapping.BaseType(column.Type)))
                throw Invalid($"Kolom '{column.Name}' bertipe {column.Type}, yang tidak didukung.");
            selected.Add(column);
        }

        PlaygroundColumn? key = null;
        if (!string.IsNullOrWhiteSpace(request.KeyColumn))
        {
            if (!byName.TryGetValue(request.KeyColumn.Trim(), out key))
                throw Invalid($"Kolom kunci '{request.KeyColumn.Trim()}' tidak ada di {detail.Schema}.{detail.Name}.");
            if (!TableModuleMapping.IsListable(TableModuleMapping.BaseType(key.Type)))
                throw Invalid($"Kolom kunci '{key.Name}' bertipe {key.Type}, yang tidak didukung.");
            if (selected.All(c => !c.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase))) selected.Insert(0, key);
        }
        if (selected.Count == 0) throw Invalid("Pilih setidaknya satu kolom.");

        var wantsWrite = request.Insert || request.Update || request.Delete;
        if (wantsWrite && !inspection.CanWrite) throw Invalid("View hanya bisa dibaca: tidak bisa membuat Tambah, Ubah, atau Hapus.");
        if ((request.Update || request.Delete) && key is null) throw Invalid("Ubah dan Hapus membutuhkan kolom kunci.");
        if (request.ForForm && (request.Update || request.Delete || !request.Insert))
            throw Invalid("Module Form hanya memakai Tambah (Insert).");
        if (wantsWrite && !CanCreateProcedures)
            throw new TableModuleException(
                "Membuat prosedur butuh Rakit:Playground:Enabled dan Rakit:Playground:AllowWrite (khusus pengembangan).",
                TableModuleErrorKind.NeedsPlayground);

        var writable = selected.Where(c => Choice(c).Writable).ToList();
        var identityKey = key is { IsIdentity: true } ? key : null;
        // Insert may also fill a non-identity key; the identity key is returned instead.
        var insertColumns = writable;
        var updateColumns = writable.Where(c => key is null || !c.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (request.Update && updateColumns.Count == 0) throw Invalid("Tidak ada kolom (selain kunci) yang bisa diubah.");
        if ((request.Insert || request.Update) && writable.Count > ModuleInputValidator.MaxFormFields)
            throw Invalid($"Form maksimal {ModuleInputValidator.MaxFormFields} field; kurangi kolom yang dipilih.");
        if (request.Insert && insertColumns.Count == 0 && identityKey is null)
            notes.Add("Tidak ada kolom yang bisa diisi: Insert hanya memakai nilai default tabel.");

        var tableName = detail.Name;
        var schema = detail.Schema;
        var stem = TableModuleMapping.CodeStem(schema, tableName, !schema.Equals(Schemas[0].Trim(), StringComparison.OrdinalIgnoreCase));
        var generated = new List<GeneratedDataSource>();

        // ---- procedures (all in one script: an error creates none of them) ----
        var procedures = new Dictionary<string, string>();   // role -> procedure name
        if (wantsWrite)
        {
            var scripts = new List<string>();
            foreach (var role in new[] { InsertRole, UpdateRole, DeleteRole })
            {
                if (!Wants(request, role)) continue;
                var proc = TableModuleMapping.ProcedureName(tableName, role.Operation);
                procedures[role.Key] = proc;

                PlaygroundObjectDetail? existing = null;
                try { existing = await catalog.GetObjectAsync(schema, proc, ct); }
                catch (SchemaObjectNotFoundException) { /* not there yet */ }

                if (existing is not null && !TableProcedureScripts.IsGenerated(existing.Definition))
                {
                    notes.Add($"Prosedur {schema}.{proc} sudah ada dan bukan buatan wizard, jadi dipakai apa adanya dan tidak ditimpa.");
                    continue;
                }
                if (existing is not null) notes.Add($"Prosedur {schema}.{proc} dibuat ulang.");

                scripts.Add(role.Key switch
                {
                    "insert" => TableProcedureScripts.Insert(schema, tableName, proc, insertColumns, identityKey),
                    "update" => TableProcedureScripts.Update(schema, tableName, proc, key!, updateColumns)!,
                    _ => TableProcedureScripts.Delete(schema, tableName, proc, key!),
                });
            }

            if (scripts.Count > 0)
            {
                var run = await playground.RunAsync(new PlaygroundRunRequest(string.Join("\nGO\n", scripts), null, null, Rollback: false), ct);
                if (!run.Success)
                    throw new TableModuleException("Prosedur gagal dibuat: " + (run.Error?.Message ?? "galat tidak diketahui"), TableModuleErrorKind.Database);
                notes.Add($"{scripts.Count} prosedur dibuat di schema {schema} dan dimasukkan ke SP Whitelist.");
            }
            foreach (var proc in procedures.Values) await whitelist.SetActiveAsync(schema, proc, true, user, ct);
        }

        // ---- data sources ----
        var columnNames = selected.Select(c => c.Name).ToList();
        if (!request.ForForm)
        {
            var sortColumn = key?.Name ?? selected.FirstOrDefault(c => TableModuleMapping.BaseType(c.Type) is not ("text" or "ntext"))?.Name;
            generated.Add(await UpsertAsync("list", $"{stem}-list", $"{tableName} - List", new DataSourceInput
            {
                Mode = DataSourceMode.Table,
                StatementType = StatementType.Query,
                Definition = new DataSourceDefinition
                {
                    Table = new TableDefinition
                    {
                        Schema = schema, Name = tableName, Columns = columnNames,
                        Sort = sortColumn is null ? [] : [new TableSort { Column = sortColumn }],
                    },
                },
            }, user, ct));

            if (key is not null)
                generated.Add(await UpsertAsync("detail", $"{stem}-detail", $"{tableName} - Detail", new DataSourceInput
                {
                    Mode = DataSourceMode.Table,
                    StatementType = StatementType.Query,
                    Definition = new DataSourceDefinition
                    {
                        Table = new TableDefinition
                        {
                            Schema = schema, Name = tableName, Columns = columnNames,
                            Filters = [new TableFilter { Column = key.Name, Operator = "=", Parameter = "id" }],
                        },
                        Parameters = [new ParameterMapping { Name = "id", Source = ParameterSource.RouteValue, Key = "id" }],
                    },
                }, user, ct));
        }

        foreach (var role in new[] { InsertRole, UpdateRole, DeleteRole })
        {
            if (!procedures.TryGetValue(role.Key, out var proc)) continue;
            var parameters = await schemaBrowser.GetProcedureParametersAsync(schema, proc, ct);
            var mappings = Mappings(role, parameters, writable, key, schema, proc, notes);
            generated.Add(await UpsertAsync(role.Key, $"{stem}-{role.Key}", $"{tableName} - {role.Label}", new DataSourceInput
            {
                Mode = DataSourceMode.StoredProcedure,
                StatementType = StatementType.Command,
                Definition = new DataSourceDefinition
                {
                    Procedure = new ProcedureReference { Schema = schema, Name = proc },
                    Parameters = mappings,
                },
            }, user, ct));
        }

        // ---- the Display / Form settings to load into the module form ----
        var config = new ModuleConfig();
        if (!request.ForForm)
        {
            config.Display.KeyField = key?.Name;
            config.Display.Columns = selected.Select(c => new ModuleColumn
            {
                Field = c.Name,
                Label = TableModuleMapping.Label(c.Name),
                Visible = true,
                Sortable = true,
                Format = TableModuleMapping.Format(TableModuleMapping.BaseType(c.Type)),
            }).ToList();
        }
        if (request.Insert || request.Update)
            config.Form.Fields = writable.Select(FormField).ToList();

        return new TableModuleResult(generated, config, notes);
    }

    private static bool Wants(TableModuleRequest request, Role role)
        => role.Key switch { "insert" => request.Insert, "update" => request.Update, _ => request.Delete };

    private static TableModuleException Invalid(string message) => new(message, TableModuleErrorKind.Invalid);

    private static ModuleFormField FormField(PlaygroundColumn c)
    {
        var type = TableModuleMapping.FieldType(c.Type);
        return new ModuleFormField
        {
            Name = c.Name,
            Label = TableModuleMapping.Label(c.Name),
            Type = type,
            Required = !c.IsNullable && c.DefaultDefinition is null,
            MaxLength = type is FormFieldType.Text or FormFieldType.Textarea ? TableModuleMapping.MaxLength(c.Type) : null,
        };
    }

    /// <summary>
    /// How a record action's data source feeds the procedure, read from the procedure's real parameters (so a procedure the
    /// wizard did not make works too): the key comes from RouteValue id, a parameter named after a writable column from the
    /// form, OUTPUT parameters are left to the data source service.
    /// </summary>
    private static List<ParameterMapping> Mappings(
        Role role, IReadOnlyList<ProcedureParameter> parameters, IReadOnlyList<PlaygroundColumn> writable,
        PlaygroundColumn? key, string schema, string proc, List<string> notes)
    {
        var mappings = new List<ParameterMapping>();
        foreach (var p in parameters.Where(p => !p.IsOutput))
        {
            var name = p.Name.TrimStart('@');
            if (role.Key != "insert" && key is not null && name.Equals(key.Name, StringComparison.OrdinalIgnoreCase))
                mappings.Add(new ParameterMapping { Name = name, Source = ParameterSource.RouteValue, Key = "id" });
            else if (writable.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } column)
                mappings.Add(new ParameterMapping { Name = name, Source = ParameterSource.FormField, Key = column.Name });
            else if (!p.HasDefault)
                notes.Add($"Parameter @{name} milik {schema}.{proc} tidak punya kolom padanan dan wajib diisi: aksi {role.Label} akan gagal sampai Anda memetakannya di Data Source.");
        }
        return mappings;
    }

    /// <summary>Creates the data source, or saves a new version when one with that code exists.</summary>
    private async Task<GeneratedDataSource> UpsertAsync(string role, string code, string name, DataSourceInput input, string user, CancellationToken ct)
    {
        input.Code = code;
        input.Name = name.Length > 200 ? name[..200] : name;

        var existing = await dataSources.GetByCodeAsync(code, ct);
        var saved = existing is null
            ? await dataSources.CreateAsync(input, user, ct)
            : await dataSources.UpdateAsync(existing.Id, input, user, ct);
        return new GeneratedDataSource(saved.Id, saved.Code, saved.Name, role, saved.Mode, existing is null);
    }
}
