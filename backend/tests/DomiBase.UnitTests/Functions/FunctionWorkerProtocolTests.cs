using System.Text.Json;
using DomiBase.Application.Functions;

namespace DomiBase.UnitTests.Functions;

public sealed class FunctionWorkerProtocolTests
{
    [Fact]
    public void Request_RoundTripsThroughWorkerProtocol()
    {
        var request = new FunctionWorkerRequest
        {
            ModuleBase64 = "AGFzbQ==",
            InputJson = "{\"a\":1}",
            ProjectId = Guid.NewGuid().ToString("D"),
            UserId = "user-1",
            Limits = new FunctionWorkerLimits
            {
                Fuel = 5000,
                MemoryPages = 8,
                TimeoutMs = 250,
                MaxOutputBytes = 1024
            }
        };

        var json = FunctionWorkerProtocol.SerializeRequest(request);
        var parsed = FunctionWorkerProtocol.ParseRequest(json);

        Assert.Equal(request.ModuleBase64, parsed.ModuleBase64);
        Assert.Equal(request.InputJson, parsed.InputJson);
        Assert.Equal(request.ProjectId, parsed.ProjectId);
        Assert.Equal(request.UserId, parsed.UserId);
        Assert.Equal(request.Limits.Fuel, parsed.Limits.Fuel);
        Assert.Equal(request.Limits.MemoryPages, parsed.Limits.MemoryPages);
        Assert.Equal(request.Limits.TimeoutMs, parsed.Limits.TimeoutMs);
        Assert.Equal(request.Limits.MaxOutputBytes, parsed.Limits.MaxOutputBytes);
        Assert.Contains("\"moduleBase64\"", json);
    }

    [Fact]
    public void Response_RoundTripsSuccessAndError()
    {
        var success = new FunctionWorkerResponse
        {
            OutputBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}")),
            FuelConsumed = 42,
            DurationMs = 7
        };

        var parsedSuccess = FunctionWorkerProtocol.ParseResponse(
            FunctionWorkerProtocol.SerializeResponse(success));

        Assert.Null(parsedSuccess.Error);
        Assert.Equal(success.OutputBase64, parsedSuccess.OutputBase64);
        Assert.Equal(42, parsedSuccess.FuelConsumed);

        var failure = new FunctionWorkerResponse
        {
            Error = new FunctionWorkerError { Code = "FUNCTION_TIMEOUT", Message = "too slow" }
        };
        var parsedFailure = FunctionWorkerProtocol.ParseResponse(
            FunctionWorkerProtocol.SerializeResponse(failure));

        Assert.Equal("FUNCTION_TIMEOUT", parsedFailure.Error!.Code);
        Assert.Equal("too slow", parsedFailure.Error.Message);
    }

    [Fact]
    public void EmptyResponse_IsRejected()
    {
        Assert.Throws<FormatException>(() => FunctionWorkerProtocol.ParseResponse(""));
        Assert.Throws<JsonException>(() => FunctionWorkerProtocol.ParseRequest("not-json"));
    }
}
