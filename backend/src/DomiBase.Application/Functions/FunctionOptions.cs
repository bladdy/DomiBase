namespace DomiBase.Application.Functions;

public sealed record FunctionOptions
{
    /// <summary>Path to the built worker DLL. The API starts it as `dotnet &lt;path&gt;`.</summary>
    public required string WorkerPath { get; init; }

    public required int WorkerStartupTimeoutMs { get; init; }

    public required int MaxModuleSizeBytes { get; init; }

    public required long DefaultFuel { get; init; }

    public required int DefaultMemoryPages { get; init; }

    public required int DefaultTimeoutMs { get; init; }

    public required int MaxInputBytes { get; init; }

    public required int MaxOutputBytes { get; init; }

    public required int MaxConcurrency { get; init; }
}
