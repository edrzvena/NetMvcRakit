using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Rakit.Core.Config;
using Rakit.Core.Data;
using Rakit.Core.DataSources;
using Rakit.Core.Health;
using Rakit.Core.Modules;
using Rakit.Core.Playground;
using Rakit.Core.Procedures;
using Rakit.Core.Schema;
using Rakit.Core.Sql;
using Rakit.Core.Workflows;

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

        services.AddOptions<RakitPlaygroundOptions>()
            .Bind(configuration.GetSection(RakitPlaygroundOptions.SectionName))
            .Validate(o => o.MaxRows is >= 1 and <= RakitPlaygroundOptions.MaxRowsLimit,
                $"Rakit:Playground:MaxRows must be between 1 and {RakitPlaygroundOptions.MaxRowsLimit}.")
            .Validate(o => o.TimeoutSeconds is >= 1 and <= RakitPlaygroundOptions.MaxTimeoutSeconds,
                $"Rakit:Playground:TimeoutSeconds must be between 1 and {RakitPlaygroundOptions.MaxTimeoutSeconds}.")
            .Validate(o => o.MaxScriptLength is >= 1000 and <= 2_000_000,
                "Rakit:Playground:MaxScriptLength must be between 1000 and 2000000.")
            .ValidateOnStart();

        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<IDbHealthService, DbHealthService>();
        services.AddSingleton<ISchemaBrowserService, SchemaBrowserService>();
        services.AddSingleton<IPlaygroundService, PlaygroundService>();
        services.AddSingleton<ISpWhitelistService, SpWhitelistService>();
        services.AddSingleton<IDataSourceRepository, DataSourceRepository>();
        services.AddSingleton<ISqlExecutionService, SqlExecutionService>();
        services.AddSingleton<IParameterResolver, ParameterResolver>();
        services.AddSingleton<ITableQueryBuilder, TableQueryBuilder>();
        services.AddSingleton<IDataSourceService, DataSourceService>();
        services.AddSingleton<IModuleRepository, ModuleRepository>();
        services.AddSingleton<IModuleGroupRepository, ModuleGroupRepository>();
        services.AddSingleton<IMenuRepository, MenuRepository>();
        services.AddSingleton<IMenuNavigationService, MenuNavigationService>();
        services.AddSingleton<IModuleRuntimeService, ModuleRuntimeService>();
        services.AddSingleton<IModuleRecordService, ModuleRecordService>();
        services.AddSingleton<IModuleLookupService, ModuleLookupService>();
        services.AddSingleton<IModuleVisualService, ModuleVisualService>();
        services.Configure<WorkflowOptions>(configuration.GetSection(WorkflowOptions.SectionName));
        services.Configure<MailOptions>(configuration.GetSection(MailOptions.SectionName));
        // Rest steps: no redirects (a redirect could leave the allowed hosts), no cookies; the step's own timeout rules, not the client's
        services.AddHttpClient(HttpRestCaller.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
            });
        services.AddSingleton<IRestCaller, HttpRestCaller>();
        services.AddSingleton<IWorkflowEmailSender, MailKitEmailSender>();
        services.AddSingleton<IModuleLinkResolver, ModuleLinkResolver>();
        services.AddSingleton<IWorkflowRepository, WorkflowRepository>();
        services.AddSingleton<IWorkflowChecker, WorkflowChecker>();
        services.AddSingleton<IWorkflowRunner, WorkflowRunner>();
        services.AddSingleton<IModuleActionService, ModuleActionService>();

        return services;
    }
}
