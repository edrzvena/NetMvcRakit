using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.DataSources;
using Rakit.Core.Health;
using Rakit.Core.Schema;
using Rakit.Core.Sql;

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
        services.AddSingleton<ISchemaBrowserService, SchemaBrowserService>();
        services.AddSingleton<IDataSourceRepository, DataSourceRepository>();
        services.AddSingleton<ISqlExecutionService, SqlExecutionService>();
        services.AddSingleton<IParameterResolver, ParameterResolver>();
        services.AddSingleton<ITableQueryBuilder, TableQueryBuilder>();

        return services;
    }
}
