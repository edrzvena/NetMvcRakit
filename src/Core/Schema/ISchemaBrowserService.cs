namespace Rakit.Core.Schema;

public interface ISchemaBrowserService
{
    Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentException">schema or name is null/blank.</exception>
    /// <exception cref="SchemaAccessDeniedException">schema is not allowed.</exception>
    /// <exception cref="SchemaObjectNotFoundException">no such table/view.</exception>
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string schema, string name, CancellationToken ct = default);

    Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentException">schema or name is null/blank.</exception>
    /// <exception cref="SchemaAccessDeniedException">schema is not allowed.</exception>
    /// <exception cref="SchemaObjectNotFoundException">no such procedure, or a system procedure (sp_*, xp_*).</exception>
    Task<ProcedureDetail> GetProcedureDetailAsync(string schema, string name, CancellationToken ct = default);
}
