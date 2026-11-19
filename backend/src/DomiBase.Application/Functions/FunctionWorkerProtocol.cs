using System.Text.Json;
using System.Text.Json.Serialization;

namespace DomiBase.Application.Functions;

/// <summary>
/// Wire protocol between the API process and the one-shot function worker.
/// The worker is a separate OS process: the API never loads a guest module.
/// </summary>
public sealed record FunctionWorkerRequest
{
    [JsonPropertyName("moduleBase64")]
    public required string ModuleBase64 { get; init; }

    [JsonPropertyName("inputJson")]
    public required string InputJson { get; init; }

    [JsonPropertyName("projectId")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("limits")]
    public required FunctionWorkerLimits Limits { get; init; }
}

public sealed record FunctionWorkerLimits
{
    [JsonPropertyName("fuel")]
    public long Fuel { get; init; }

    [JsonPropertyName("memoryPages")]
    public int MemoryPages { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; init; }

    [JsonPropertyName("maxOutputBytes")]
    public int MaxOutputBytes { get; init; }
}

public sealed record FunctionWorkerResponse
{
    [JsonPropertyName("outputBase64")]
    public string? OutputBase64 { get; init; }

    [JsonPropertyName("fuelConsumed")]
    public long FuelConsumed { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("error")]
    public FunctionWorkerError? Error { get; init; }
}

public sealed record FunctionWorkerError
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public static class FunctionWorkerProtocol
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string SerializeRequest(FunctionWorkerRequest request) =>
        JsonSerializer.Serialize(request, Options);

    public static FunctionWorkerRequest ParseRequest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("The worker request is empty.");
        }

        return JsonSerializer.Deserialize<FunctionWorkerRequest>(json, Options)
            ?? throw new FormatException("The worker request is empty.");
    }

    public static string SerializeResponse(FunctionWorkerResponse response) =>
        JsonSerializer.Serialize(response, Options);

    public static FunctionWorkerResponse ParseResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("The worker response is empty.");
        }

        return JsonSerializer.Deserialize<FunctionWorkerResponse>(json, Options)
            ?? throw new FormatException("The worker response is empty.");
    }
}
