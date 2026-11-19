using DomiBase.Application.Storage;
using DomiBase.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddDomiBaseStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(CreateOptions(configuration));
        services.AddScoped<IStorageRepository, EfStorageRepository>();
        services.AddSingleton<IStorageBackend, FileSystemStorageBackend>();
        services.AddScoped<StorageService>();

        return services;
    }

    private static StorageOptions CreateOptions(IConfiguration configuration)
    {
        var root = configuration["STORAGE_ROOT"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "storage");
        }

        var maxSizeText = configuration["STORAGE_MAX_OBJECT_SIZE_BYTES"];
        if (!long.TryParse(maxSizeText, out var maxSize) || maxSize <= 0)
        {
            maxSize = 10 * 1024 * 1024; // 10 MB default
        }

        var signingKey = configuration["STORAGE_SIGNING_KEY"];
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            // Fallback to a derived key for local development only. In production this must be set explicitly.
            signingKey = "domibase-storage-local-signing-key-change-me";
        }

        return new StorageOptions
        {
            RootDirectory = root,
            MaxObjectSizeBytes = maxSize,
            SigningKey = signingKey
        };
    }
}
