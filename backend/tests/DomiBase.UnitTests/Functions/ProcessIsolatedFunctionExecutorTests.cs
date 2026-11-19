using DomiBase.Application.Functions;
using DomiBase.Infrastructure.Functions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DomiBase.UnitTests.Functions;

public sealed class ProcessIsolatedFunctionExecutorTests
{
    [Fact]
    public async Task Timeout_KillsTheWorkerProcessAndReturnsFunctionTimeout()
    {
        var executor = CreateExecutor(workerStartupTimeoutMs: 100);
        var request = CreateRequest("sleep");

        var result = await executor.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("FUNCTION_TIMEOUT", result.Code);
    }

    [Fact]
    public async Task NonZeroExit_IsReportedAsWorkerError()
    {
        var executor = CreateExecutor(workerStartupTimeoutMs: 1000);
        var request = CreateRequest("crash");

        var result = await executor.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("WORKER_ERROR", result.Code);
    }

    [Fact]
    public async Task MissingWorker_IsReportedAsUnavailable()
    {
        var options = new FunctionOptions
        {
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "does-not-exist.dll"),
            WorkerStartupTimeoutMs = 1000,
            MaxModuleSizeBytes = 1024,
            DefaultFuel = 1000,
            DefaultMemoryPages = 1,
            DefaultTimeoutMs = 100,
            MaxInputBytes = 1024,
            MaxOutputBytes = 1024,
            MaxConcurrency = 1
        };
        var executor = new ProcessIsolatedFunctionExecutor(options, NullLogger<ProcessIsolatedFunctionExecutor>.Instance);

        var result = await executor.ExecuteAsync(CreateRequest("sleep"));

        Assert.False(result.Succeeded);
        Assert.Equal("FUNCTION_UNAVAILABLE", result.Code);
    }

    private static ProcessIsolatedFunctionExecutor CreateExecutor(int workerStartupTimeoutMs)
    {
        var options = new FunctionOptions
        {
            WorkerPath = Path.Combine(AppContext.BaseDirectory, "DomiBase.Functions.TestWorker.dll"),
            WorkerStartupTimeoutMs = workerStartupTimeoutMs,
            MaxModuleSizeBytes = 1024,
            DefaultFuel = 1000,
            DefaultMemoryPages = 1,
            DefaultTimeoutMs = 200,
            MaxInputBytes = 1024,
            MaxOutputBytes = 1024,
            MaxConcurrency = 1
        };

        return new ProcessIsolatedFunctionExecutor(
            options,
            NullLogger<ProcessIsolatedFunctionExecutor>.Instance);
    }

    private static FunctionExecutionRequest CreateRequest(string mode) =>
        new()
        {
            Module = [0, 97, 115, 109],
            InputJson = $"{{\"mode\":\"{mode}\"}}",
            ProjectId = Guid.NewGuid(),
            UserId = null,
            Limits = new FunctionWorkerLimits
            {
                Fuel = 1000,
                MemoryPages = 1,
                TimeoutMs = 200,
                MaxOutputBytes = 1024
            }
        };
}
