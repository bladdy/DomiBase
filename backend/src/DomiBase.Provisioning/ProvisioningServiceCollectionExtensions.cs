using DomiBase.Application.Extensions;
using DomiBase.Application.Projects;
using DomiBase.Application.Query;
using DomiBase.Application.Schema;
using DomiBase.Provisioning.Admin;
using DomiBase.Provisioning.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.Provisioning;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the privileged provisioning connection factory. Fails fast when
    /// <c>PROVISIONING_DB_CONNECTION_STRING</c> is missing.
    /// </summary>
    public static IServiceCollection AddDomiBaseProvisioning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration["PROVISIONING_DB_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "PROVISIONING_DB_CONNECTION_STRING is not configured. Add it to the local .env file or set it as an environment variable.");
        }

        services.AddSingleton(new ProvisioningOptions
        {
            ConnectionString = connectionString,
            AdminDatabase = configuration["PROVISIONING_ADMIN_DATABASE"] ?? "postgres"
        });

        services.AddSingleton<IProvisioningConnectionFactory, ProvisioningConnectionFactory>();
        services.AddSingleton<IProjectProvisioner, ProjectProvisioner>();
        services.AddSingleton<IProjectSchemaAdmin, ProjectSchemaAdmin>();
        services.AddSingleton<IMigrationStore, ProjectMigrationStore>();
        services.AddSingleton<IProjectQueryRunner, ProjectQueryRunner>();
        services.AddSingleton<IProjectExtensionAdmin, ProjectExtensionAdmin>();
        services.AddSingleton<IProjectDatabaseCoordinator, ProjectDatabaseCoordinator>();

        return services;
    }
}
