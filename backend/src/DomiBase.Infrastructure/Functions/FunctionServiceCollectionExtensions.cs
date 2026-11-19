using DomiBase.Application.Functions;
using DomiBase.Infrastructure.Functions;
using DomiBase.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DomiBase.Infrastructure.Functions;

public static class FunctionServiceCollectionExtensions
{
    public static IServiceCollection AddDomiBaseFunctions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(CreateOptions(configuration));
        services.AddScoped<IFunctionRepository, EfFunctionRepository>();
        services.AddSingleton<IFunctionExecutor, ProcessIsolatedFunctionExecutor>();
        services.AddScoped<FunctionService>();

        return services;
    }

    private static FunctionOptions CreateOptions(IConfiguration configuration)
    {
        var configured = configuration["DOMIBASE_FUNCTIONS_WORKER"];
        var workerPath = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "DomiBase.Functions.Worker.dll")
            : Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(configured, AppContext.BaseDirectory);

        return new FunctionOptions
        {
            WorkerPath = workerPath,
            WorkerStartupTimeoutMs = ReadPositiveInt(configuration["FUNCTIONS_WORKER_STARTUP_TIMEOUT_MS"], 5_000),
            MaxModuleSizeBytes = ReadPositiveInt(configuration["FUNCTIONS_MAX_MODULE_SIZE_BYTES"], 5 * 1024 * 1024),
            DefaultFuel = ReadPositiveLong(configuration["FUNCTIONS_DEFAULT_FUEL"], 1_000_000),
            DefaultMemoryPages = ReadPositiveInt(configuration["FUNCTIONS_DEFAULT_MEMORY_PAGES"], 16),
            DefaultTimeoutMs = ReadPositiveInt(configuration["FUNCTIONS_DEFAULT_TIMEOUT_MS"], 1_000),
            MaxInputBytes = ReadPositiveInt(configuration["FUNCTIONS_MAX_INPUT_BYTES"], 65_536),
            MaxOutputBytes = ReadPositiveInt(configuration["FUNCTIONS_MAX_OUTPUT_BYTES"], 65_536),
            MaxConcurrency = ReadPositiveInt(configuration["FUNCTIONS_MAX_CONCURRENCY"], 4)
        };
    }

    private static int ReadPositiveInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    private static long ReadPositiveLong(string? raw, long fallback) =>
        long.TryParse(raw, out var value) && value > 0 ? value : fallback;
}
