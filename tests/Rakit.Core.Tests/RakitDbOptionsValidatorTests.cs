using Rakit.Core.Config;

namespace Rakit.Core.Tests;

public class RakitDbOptionsValidatorTests
{
    private static RakitDbOptions Valid() => new()
    {
        MetadataConnectionString = "Server=x;Database=y",
        ExecutionConnectionString = "Server=x;Database=y;Pooling=false",
        AllowedSchemas = ["app"],
    };

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(RakitDbOptions o)
        => new RakitDbOptionsValidator().Validate(null, o);

    [Fact]
    public void Valid_options_pass() => Assert.True(Validate(Valid()).Succeeded);

    [Fact]
    public void Empty_metadata_connection_string_fails()
    {
        var o = Valid(); o.MetadataConnectionString = "";
        var r = Validate(o);
        Assert.True(r.Failed);
        Assert.Contains(nameof(RakitDbOptions.MetadataConnectionString), r.FailureMessage);
    }

    [Fact]
    public void Empty_execution_connection_string_fails()
    {
        var o = Valid(); o.ExecutionConnectionString = " ";
        var r = Validate(o);
        Assert.True(r.Failed);
        Assert.Contains(nameof(RakitDbOptions.ExecutionConnectionString), r.FailureMessage);
    }

    [Fact]
    public void Empty_allowed_schemas_fails()
    {
        var o = Valid(); o.AllowedSchemas = [];
        var r = Validate(o);
        Assert.True(r.Failed);
        Assert.Contains(nameof(RakitDbOptions.AllowedSchemas), r.FailureMessage);
    }

    [Theory]
    [InlineData("rakit")]
    [InlineData("RAKIT")]
    [InlineData("sys")]
    [InlineData("INFORMATION_SCHEMA")]
    public void Forbidden_schema_fails(string schema)
    {
        var o = Valid(); o.AllowedSchemas = ["app", schema];
        var r = Validate(o);
        Assert.True(r.Failed);
        Assert.Contains(nameof(RakitDbOptions.AllowedSchemas), r.FailureMessage);
    }

    [Fact]
    public void Blank_schema_entry_fails()
    {
        var o = Valid(); o.AllowedSchemas = ["app", " "];
        var r = Validate(o);
        Assert.True(r.Failed);
        Assert.Contains(nameof(RakitDbOptions.AllowedSchemas), r.FailureMessage);
    }
}
