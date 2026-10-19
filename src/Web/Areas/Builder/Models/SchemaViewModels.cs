using Rakit.Core.Schema;

namespace Rakit.Web.Areas.Builder.Models;

public sealed record SchemaIndexViewModel(IReadOnlyList<TableInfo> Tables, IReadOnlyList<ProcedureInfo> Procedures);

public sealed record TableDetailViewModel(string Schema, string Name, IReadOnlyList<ColumnInfo> Columns);

public static class SchemaDisplay
{
    /// <summary>Human readable SQL type, e.g. nvarchar(100), decimal(10,2), nvarchar(max).</summary>
    public static string TypeLabel(string dataType, int maxLength, byte precision, byte scale)
    {
        switch (dataType.ToLowerInvariant())
        {
            case "nvarchar":
            case "nchar":
                return maxLength == -1 ? $"{dataType}(max)" : $"{dataType}({maxLength / 2})";
            case "varchar":
            case "char":
            case "varbinary":
            case "binary":
                return maxLength == -1 ? $"{dataType}(max)" : $"{dataType}({maxLength})";
            case "decimal":
            case "numeric":
                return $"{dataType}({precision},{scale})";
            default:
                return dataType;
        }
    }
}
