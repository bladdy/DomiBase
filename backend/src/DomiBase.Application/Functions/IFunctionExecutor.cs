namespace DomiBase.Application.Functions;

/// <summary>
/// Executes a function module in an isolated process. Implemented by the API
/// through a one-shot worker; never by loading user code into the host.
/// </summary>
public interface IFunctionExecutor
{
    Task<FunctionExecutionResult> ExecuteAsync(
        FunctionExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record FunctionExecutionRequest
{
    public required byte[] Module { get; init; }

    public required string InputJson { get; init; }

    public required Guid ProjectId { get; init; }

    public string? UserId { get; init; }

    public required FunctionWorkerLimits Limits { get; init; }
}

public sealed record FunctionExecutionResult
{
    public bool Succeeded { get; init; }

    /// <summary>Serialized JSON returned by the guest module.</summary>
    public string? OutputJson { get; init; }

    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public long FuelConsumed { get; init; }

    public long DurationMs { get; init; }

    public static FunctionExecutionResult Ok(string outputJson, long fuelConsumed, long durationMs) =>
        new()
        {
            Succeeded = true,
            OutputJson = outputJson,
            FuelConsumed = fuelConsumed,
            DurationMs = durationMs
        };

    public static FunctionExecutionResult Fail(
        string code,
        string message,
        long fuelConsumed = 0,
        long durationMs = 0) =>
        new()
        {
            Succeeded = false,
            Code = code,
            Message = message,
            FuelConsumed = fuelConsumed,
            DurationMs = durationMs
        };
}
