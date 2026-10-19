using Microsoft.Extensions.Options;

namespace Rakit.Core.Config;

public sealed class RakitDbOptionsValidator : IValidateOptions<RakitDbOptions>
{
    private static readonly string[] ForbiddenSchemas = ["rakit", "sys", "INFORMATION_SCHEMA"];

    public ValidateOptionsResult Validate(string? name, RakitDbOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.MetadataConnectionString))
            failures.Add($"{nameof(options.MetadataConnectionString)} is required (ConnectionStrings:Metadata).");

        if (string.IsNullOrWhiteSpace(options.ExecutionConnectionString))
            failures.Add($"{nameof(options.ExecutionConnectionString)} is required (ConnectionStrings:Execution).");

        if (options.AllowedSchemas is null || options.AllowedSchemas.Length == 0)
        {
            failures.Add($"{nameof(options.AllowedSchemas)} must contain at least one schema.");
        }
        else
        {
            foreach (var schema in options.AllowedSchemas)
            {
                if (string.IsNullOrWhiteSpace(schema))
                    failures.Add($"{nameof(options.AllowedSchemas)} contains a blank entry.");
                else if (ForbiddenSchemas.Contains(schema.Trim(), StringComparer.OrdinalIgnoreCase))
                    failures.Add($"{nameof(options.AllowedSchemas)} must not contain '{schema}'.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
