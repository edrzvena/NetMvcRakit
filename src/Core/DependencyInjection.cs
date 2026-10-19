using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.Health;

namespace Rakit.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddRakitCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RakitDbOptions>()
            .Bind(configuration.GetSection(RakitDbOptions.SectionName))
            .PostConfigure(o =>
            {
                o.MetadataConnectionString = configuration.GetConnectionString("Metadata") ?? "";
                o.ExecutionConnectionString = configuration.GetConnectionString("Execution") ?? "";
            })
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<RakitDbOptions>, RakitDbOptionsValidator>();

        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<IDbHealthService, DbHealthService>();

        return services;
    }
}
