using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.Schema;

namespace Rakit.Core.Tests;

[Collection(DbCollection.Name)]
public class SchemaBrowserServiceTests
{
    private static readonly object Gate = new();
    private static bool _seeded;

    public SchemaBrowserServiceTests()
    {
        lock (Gate)
        {
            if (_seeded) return;
            // Each statement is its own batch (CREATE OR ALTER must be first in a batch).
            Admin("IF OBJECT_ID(N'rakit.Probe', N'U') IS NULL CREATE TABLE rakit.Probe (Id int)");
            Admin("IF SCHEMA_ID(N'empty_schema') IS NULL EXEC(N'CREATE SCHEMA empty_schema')");
            Admin("CREATE OR ALTER VIEW app.vCustomerNames AS SELECT Id, Name FROM app.Customers");
            Admin("CREATE OR ALTER PROCEDURE app.sp_hidden AS SELECT 1 AS A");
            Admin("CREATE OR ALTER PROCEDURE app.xp_hidden AS SELECT 1 AS A");
            Admin("CREATE OR ALTER PROCEDURE app.SpOut_Test @X int OUTPUT AS SET @X = 1");
            // SQL built in a variable cannot be analysed (error 11514); a literal EXEC(N'...') can.
            Admin(@"CREATE OR ALTER PROCEDURE app.SpDynamic_Test @T sysname = N'Customers'
                    AS BEGIN DECLARE @sql nvarchar(max) = N'SELECT * FROM app.' + QUOTENAME(@T); EXEC(@sql); END");
            Admin(@"CREATE OR ALTER PROCEDURE app.SpDefaults_Test
                        @A decimal(10,2) = 0, @B int, @C nvarchar(10) = N'x' OUTPUT
                    AS SELECT 1 AS A");
            _seeded = true;
        }
    }

    private static void Admin(string sql)
    {
        using var conn = new SqlConnection(TestDb.AdminConnectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static SchemaBrowserService Create(params string[] allowed)
    {
        var options = TestDb.Options;
        if (allowed.Length > 0) options.AllowedSchemas = allowed;
        return new SchemaBrowserService(new DbConnectionFactory(Options.Create(options)), Options.Create(options));
    }

    private static async Task AssertSchemaRejectedAsync(Func<Task> action)
    {
        var ex = await Record.ExceptionAsync(action);
        Assert.True(
            ex is SchemaAccessDeniedException or SchemaObjectNotFoundException,
            $"Expected a schema exception but got: {ex?.GetType().Name ?? "no exception"}");
    }

    // ---- listing -------------------------------------------------------------------------------

    [Fact]
    public async Task Tables_lists_app_customers_only_in_allowed_schemas()
    {
        var tables = await Create().GetTablesAsync();

        Assert.Contains(new TableInfo("app", "Customers", "TABLE"), tables);
        Assert.All(tables, t => Assert.Equal("app", t.Schema));
    }

    [Fact]
    public async Task Views_are_listed_with_kind_VIEW()
    {
        var tables = await Create().GetTablesAsync();
        Assert.Contains(new TableInfo("app", "vCustomerNames", "VIEW"), tables);
    }

    [Fact]
    public async Task Columns_of_Customers()
    {
        var cols = await Create().GetColumnsAsync("app", "Customers");

        Assert.Equal(["Id", "Name", "Email", "Created"], cols.Select(c => c.Name));
        var name = cols.Single(c => c.Name == "Name");
        Assert.Equal("nvarchar", name.DataType);
        Assert.Equal(200, name.MaxLength); // nvarchar(100) = 200 bytes
        Assert.False(name.IsNullable);
        Assert.True(cols.Single(c => c.Name == "Email").IsNullable);
        Assert.Equal("datetime2", cols.Single(c => c.Name == "Created").DataType);
    }

    [Fact]
    public async Task Procedures_include_SpCustomers_List_and_exclude_system_names()
    {
        var procs = await Create().GetProceduresAsync();

        Assert.Contains(new ProcedureInfo("app", "SpCustomers_List"), procs);
        Assert.DoesNotContain(procs, p => p.Name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(procs, p => p.Name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Empty_allowed_schema_returns_empty_lists_not_errors()
    {
        var service = Create("empty_schema");

        Assert.Empty(await service.GetTablesAsync());
        Assert.Empty(await service.GetProceduresAsync());
    }

    // ---- procedure detail -----------------------------------------------------------------------

    [Fact]
    public async Task Procedure_detail_has_all_parameters()
    {
        var detail = await Create().GetProcedureDetailAsync("app", "SpCustomers_List");

        Assert.Equal(["@IsCount", "@Skip", "@Take", "@OrderBy", "@Search"], detail.Parameters.Select(p => p.Name));
        Assert.Equal(["bit", "int", "int", "nvarchar", "nvarchar"], detail.Parameters.Select(p => p.DataType));
        Assert.All(detail.Parameters, p =>
        {
            Assert.True(p.HasDefault);
            Assert.False(p.IsOutput);
        });
    }

    [Fact]
    public async Task Procedure_detail_describes_first_result_set()
    {
        var detail = await Create().GetProcedureDetailAsync("app", "SpCustomers_Detail");

        Assert.Equal(["Id", "Name", "Email", "Created"], detail.ResultColumns.Select(c => c.Name));
        Assert.Null(detail.ResultSetWarning);
    }

    [Fact]
    public async Task Procedure_with_branching_result_sets_returns_warning()
    {
        // SpCustomers_List returns COUNT(...) when @IsCount = 1 and rows otherwise: SQL Server cannot
        // infer a single result set shape (error 11512), so the browser must warn instead of failing.
        var detail = await Create().GetProcedureDetailAsync("app", "SpCustomers_List");

        Assert.Empty(detail.ResultColumns);
        Assert.False(string.IsNullOrWhiteSpace(detail.ResultSetWarning));
        Assert.Equal(5, detail.Parameters.Count);
    }

    [Fact]
    public async Task Procedure_with_output_param_is_flagged()
    {
        var detail = await Create().GetProcedureDetailAsync("app", "SpOut_Test");

        var x = Assert.Single(detail.Parameters);
        Assert.Equal("@X", x.Name);
        Assert.True(x.IsOutput);
        Assert.False(x.HasDefault);
    }

    [Fact]
    public async Task Default_detection_handles_precision_types_and_params_without_default()
    {
        var detail = await Create().GetProcedureDetailAsync("app", "SpDefaults_Test");

        Assert.True(detail.Parameters.Single(p => p.Name == "@A").HasDefault);
        Assert.False(detail.Parameters.Single(p => p.Name == "@B").HasDefault);
        var c = detail.Parameters.Single(p => p.Name == "@C");
        Assert.True(c.HasDefault);
        Assert.True(c.IsOutput);
    }

    // Review Focus 4
    [Fact]
    public async Task Procedure_with_undescribable_result_set_returns_warning()
    {
        var detail = await Create().GetProcedureDetailAsync("app", "SpDynamic_Test");

        Assert.Empty(detail.ResultColumns);
        Assert.False(string.IsNullOrWhiteSpace(detail.ResultSetWarning));
    }

    // ---- access rules ---------------------------------------------------------------------------

    [Fact]
    public async Task Disallowed_schema_is_denied()
    {
        await Assert.ThrowsAsync<SchemaAccessDeniedException>(
            () => Create().GetColumnsAsync("rakit", "Probe"));
    }

    [Fact]
    public async Task System_procedure_name_is_not_found()
    {
        await Assert.ThrowsAsync<SchemaObjectNotFoundException>(
            () => Create().GetProcedureDetailAsync("app", "sp_hidden"));
    }

    [Fact]
    public async Task Unknown_object_is_not_found()
    {
        await Assert.ThrowsAsync<SchemaObjectNotFoundException>(
            () => Create().GetColumnsAsync("app", "NoSuchThing"));
        await Assert.ThrowsAsync<SchemaObjectNotFoundException>(
            () => Create().GetProcedureDetailAsync("app", "NoSuchThing"));
    }

    // Review Focus 1
    [Theory]
    [InlineData("app'; DROP TABLE app.Customers--", "Customers")]
    [InlineData("app]", "Customers")]
    [InlineData("app", "app.Customers")]
    [InlineData("app", "Customers; SELECT 1")]
    [InlineData("app", "Customers' OR '1'='1")]
    public async Task Injection_like_inputs_are_rejected_not_executed(string schema, string name)
    {
        var service = Create();

        await AssertSchemaRejectedAsync(() => service.GetColumnsAsync(schema, name));
        await AssertSchemaRejectedAsync(() => service.GetProcedureDetailAsync(schema, name));

        using var conn = new SqlConnection(TestDb.AdminConnectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM app.Customers", conn);
        Assert.True((int)cmd.ExecuteScalar()! >= 5);
    }

    // Review Focus 2
    [Fact]
    public async Task Lookup_is_case_insensitive()
    {
        var service = Create();

        var upper = await service.GetColumnsAsync("APP", "customers");
        var exact = await service.GetColumnsAsync("app", "Customers");
        Assert.Equal(exact, upper);

        var detail = await service.GetProcedureDetailAsync("App", "spcustomers_list");
        Assert.Equal("app", detail.Schema);
        Assert.Equal("SpCustomers_List", detail.Name);
    }

    // Review Focus 3
    [Theory]
    [InlineData(null, "Customers")]
    [InlineData("", "Customers")]
    [InlineData("  ", "Customers")]
    [InlineData("app", null)]
    [InlineData("app", "")]
    [InlineData("app", "  ")]
    public async Task Blank_inputs_throw_ArgumentException(string? schema, string? name)
    {
        var service = Create();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.GetColumnsAsync(schema!, name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.GetProcedureDetailAsync(schema!, name!));
    }
}
