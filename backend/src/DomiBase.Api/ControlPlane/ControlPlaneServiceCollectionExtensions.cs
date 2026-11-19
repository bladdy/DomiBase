using DomiBase.Application.ApiKeys;
using DomiBase.Application.Auditing;
using DomiBase.Application.Backups;
using DomiBase.Application.Data;
using DomiBase.Application.Extensions;
using DomiBase.Application.Projects;
using DomiBase.Application.Query;
using DomiBase.Application.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomiBase.Api.ControlPlane;

public static class ControlPlaneServiceCollectionExtensions
{
    private const string KeyringVariable = "DOMIBASE_API_KEY_PEPPERS";
    private const string LegacyVariable = "DOMIBASE_API_KEY_PEPPER";

    /// <summary>
    /// Registers Control Plane application services and the API key generator.
    /// The pepper keyring is comma separated; the first entry hashes new keys and every
    /// remaining entry (including the legacy single pepper) still verifies old ones.
    /// Fails fast when no valid pepper is configured.
    /// </summary>
    public static IServiceCollection AddDomiBaseControlPlane(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var peppers = ReadPeppers(configuration);

        services.AddSingleton<IApiKeyGenerator>(new ApiKeyGenerator(new ApiKeyGeneratorOptions
        {
            Pepper = peppers[0],
            AdditionalPeppers = peppers.Skip(1).ToArray()
        }));
        services.AddScoped<ProjectService>();
        services.AddScoped<SchemaService>();
        services.AddScoped<SchemaAdminService>();
        services.AddScoped<ProjectSchemaResolver>();
        services.AddScoped<MigrationService>();
        services.AddScoped<SqlQueryService>();
        services.AddScoped<DataPreviewService>();
        services.AddScoped<DataEditorService>();
        services.AddScoped<ExtensionService>();
        services.AddScoped<BackupService>();
        services.AddScoped<AuditService>();

        return services;
    }

    private static List<string> ReadPeppers(IConfiguration configuration)
    {
        var peppers = (configuration[KeyringVariable] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var legacy = configuration[LegacyVariable];
        if (!string.IsNullOrWhiteSpace(legacy) && !peppers.Contains(legacy, StringComparer.Ordinal))
        {
            // The legacy pepper is always retired, never active after an explicit keyring.
            peppers.Add(legacy);
        }

        if (peppers.Count == 0)
        {
            throw new InvalidOperationException(
                $"No API key pepper is configured. Set {KeyringVariable} or {LegacyVariable}.");
        }

        if (peppers.Any(pepper => pepper.Length < ApiKeyGeneratorOptions.MinimumPepperLength))
        {
            throw new InvalidOperationException(
                $"Every pepper must be at least {ApiKeyGeneratorOptions.MinimumPepperLength} characters long.");
        }

        if (peppers.Count != peppers.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException("API key peppers must be distinct.");
        }

        return peppers;
    }
}
