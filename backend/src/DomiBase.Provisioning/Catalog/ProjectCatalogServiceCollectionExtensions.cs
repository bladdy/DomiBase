using DomiBase.Application.Extensions;
using DomiBase.Application.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.Provisioning.Catalog;

public static class ProjectCatalogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the read-only catalog readers used by the Control Plane schema inspector and
    /// extension catalog. The privileged connection string is already validated by
    /// <c>AddDomiBaseProvisioning</c>, which must be called first.
    /// </summary>
    public static IServiceCollection AddDomiBaseProjectCatalog(this IServiceCollection services)
    {
        services.AddSingleton<IProjectCatalogReader, ProjectCatalogReader>();
        services.AddSingleton<IProjectExtensionCatalog, ProjectExtensionCatalogReader>();
        return services;
    }
}
