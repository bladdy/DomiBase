using System.Security.Cryptography;
using DomiBase.Domain.Functions;

namespace DomiBase.Application.Functions;

/// <summary>
/// Control-plane registration and runtime invocation of project functions.
/// The service itself never executes a module; it delegates to IFunctionExecutor.
/// </summary>
public sealed class FunctionService(
    IFunctionRepository repository,
    IFunctionExecutor executor,
    FunctionOptions options)
{
    private const int MaxNameLength = 63;
    private const int MaxDescriptionLength = 256;
    private const long MaxFuel = 10_000_000;
    private const int MaxMemoryPages = 256;
    private const int MaxTimeoutMs = 10_000;
    private const int MaxInputBytes = 262_144;
    private const int MaxOutputBytes = 262_144;

    public async Task<IReadOnlyList<FunctionDefinition>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await repository.ListAsync(projectId, cancellationToken);

    public async Task<FunctionResult<FunctionDefinition>> CreateAsync(
        Guid projectId,
        string name,
        string? description,
        string moduleBase64,
        FunctionLimitInput? limits,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        if (normalizedName.Succeeded == false)
        {
            return FunctionResult<FunctionDefinition>.Fail(normalizedName.Code, normalizedName.Message);
        }

        if (description is { Length: > MaxDescriptionLength })
        {
            return FunctionResult<FunctionDefinition>.Fail(
                "INVALID_DESCRIPTION",
                $"Description must not exceed {MaxDescriptionLength} characters.");
        }

        byte[] module;
        try
        {
            module = Convert.FromBase64String(moduleBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return FunctionResult<FunctionDefinition>.Fail("INVALID_MODULE", "moduleBase64 is not valid base64.");
        }

        if (module.Length == 0)
        {
            return FunctionResult<FunctionDefinition>.Fail("INVALID_MODULE", "The module is empty.");
        }

        if (module.Length > options.MaxModuleSizeBytes)
        {
            return FunctionResult<FunctionDefinition>.Fail(
                "MODULE_TOO_LARGE",
                $"The module exceeds the maximum size of {options.MaxModuleSizeBytes} bytes.");
        }

        if (await repository.FindByNameAsync(projectId, normalizedName.Value!, cancellationToken) is not null)
        {
            return FunctionResult<FunctionDefinition>.Fail(
                "FUNCTION_ALREADY_EXISTS",
                "A function with this name already exists in the project.");
        }

        var resolvedLimits = ResolveLimits(limits);
        if (resolvedLimits.Succeeded == false)
        {
            return FunctionResult<FunctionDefinition>.Fail(resolvedLimits.Code, resolvedLimits.Message);
        }

        var definition = new FunctionDefinition
        {
            ProjectId = projectId,
            Name = normalizedName.Value!,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Module = module,
            ModuleSha256 = Convert.ToHexString(SHA256.HashData(module)).ToLowerInvariant(),
            Fuel = resolvedLimits.Value!.Fuel,
            MemoryPages = resolvedLimits.Value.MemoryPages,
            TimeoutMs = resolvedLimits.Value.TimeoutMs,
            MaxInputBytes = resolvedLimits.Value.MaxInputBytes,
            MaxOutputBytes = resolvedLimits.Value.MaxOutputBytes
        };

        await repository.CreateAsync(definition, cancellationToken);
        return FunctionResult<FunctionDefinition>.Ok(definition);
    }

    public async Task<FunctionResult<bool>> DeleteAsync(
        Guid projectId,
        Guid functionId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await repository.DeleteAsync(projectId, functionId, cancellationToken);
        return deleted
            ? FunctionResult<bool>.Ok(true)
            : FunctionResult<bool>.Fail("FUNCTION_NOT_FOUND", "Function not found.");
    }

    public async Task<FunctionResult<string>> InvokeAsync(
        Guid projectId,
        string name,
        string inputJson,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        if (normalizedName.Succeeded == false)
        {
            return FunctionResult<string>.Fail(normalizedName.Code, normalizedName.Message);
        }

        var definition = await repository.FindByNameAsync(projectId, normalizedName.Value!, cancellationToken);
        if (definition is null)
        {
            return FunctionResult<string>.Fail("FUNCTION_NOT_FOUND", "Function not found.");
        }

        if (!definition.Enabled)
        {
            return FunctionResult<string>.Fail("FUNCTION_DISABLED", "The function is disabled.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(inputJson) > definition.MaxInputBytes)
        {
            return FunctionResult<string>.Fail("INPUT_TOO_LARGE", "The function input is too large.");
        }

        var execution = await executor.ExecuteAsync(
            new FunctionExecutionRequest
            {
                Module = definition.Module,
                InputJson = inputJson,
                ProjectId = projectId,
                UserId = userId,
                Limits = new FunctionWorkerLimits
                {
                    Fuel = definition.Fuel,
                    MemoryPages = definition.MemoryPages,
                    TimeoutMs = definition.TimeoutMs,
                    MaxOutputBytes = definition.MaxOutputBytes
                }
            },
            cancellationToken);

        return execution.Succeeded
            ? FunctionResult<string>.Ok(execution.OutputJson!)
            : FunctionResult<string>.Fail(execution.Code, execution.Message);
    }

    private FunctionResult<FunctionLimitValues> ResolveLimits(FunctionLimitInput? input)
    {
        var fuel = input?.Fuel ?? options.DefaultFuel;
        var memoryPages = input?.MemoryPages ?? options.DefaultMemoryPages;
        var timeoutMs = input?.TimeoutMs ?? options.DefaultTimeoutMs;
        var maxInputBytes = input?.MaxInputBytes ?? options.MaxInputBytes;
        var maxOutputBytes = input?.MaxOutputBytes ?? options.MaxOutputBytes;

        if (fuel <= 0 || fuel > MaxFuel)
        {
            return FunctionResult<FunctionLimitValues>.Fail("INVALID_LIMITS", $"fuel must be between 1 and {MaxFuel}.");
        }

        if (memoryPages <= 0 || memoryPages > MaxMemoryPages)
        {
            return FunctionResult<FunctionLimitValues>.Fail(
                "INVALID_LIMITS",
                $"memoryPages must be between 1 and {MaxMemoryPages}.");
        }

        if (timeoutMs <= 0 || timeoutMs > MaxTimeoutMs)
        {
            return FunctionResult<FunctionLimitValues>.Fail(
                "INVALID_LIMITS",
                $"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        }

        if (maxInputBytes <= 0 || maxInputBytes > MaxInputBytes)
        {
            return FunctionResult<FunctionLimitValues>.Fail(
                "INVALID_LIMITS",
                $"maxInputBytes must be between 1 and {MaxInputBytes}.");
        }

        if (maxOutputBytes <= 0 || maxOutputBytes > MaxOutputBytes)
        {
            return FunctionResult<FunctionLimitValues>.Fail(
                "INVALID_LIMITS",
                $"maxOutputBytes must be between 1 and {MaxOutputBytes}.");
        }

        return FunctionResult<FunctionLimitValues>.Ok(new FunctionLimitValues(
            fuel, memoryPages, timeoutMs, maxInputBytes, maxOutputBytes));
    }

    private static FunctionResult<string> NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return FunctionResult<string>.Fail("INVALID_FUNCTION_NAME", "Function name must be between 1 and 63 characters.");
        }

        var normalized = name.Trim().ToLowerInvariant();
        if (!normalized.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
        {
            return FunctionResult<string>.Fail(
                "INVALID_FUNCTION_NAME",
                "Function name may only contain lowercase letters, digits, hyphens and underscores.");
        }

        return FunctionResult<string>.Ok(normalized);
    }
}

public sealed record FunctionLimitInput
{
    public long? Fuel { get; init; }
    public int? MemoryPages { get; init; }
    public int? TimeoutMs { get; init; }
    public int? MaxInputBytes { get; init; }
    public int? MaxOutputBytes { get; init; }
}

internal sealed record FunctionLimitValues(
    long Fuel,
    int MemoryPages,
    int TimeoutMs,
    int MaxInputBytes,
    int MaxOutputBytes);

public sealed class FunctionResult<T>
{
    public bool Succeeded { get; }
    public T? Value { get; }
    public string Code { get; }
    public string Message { get; }

    private FunctionResult(T? value, bool succeeded, string code, string message)
    {
        Value = value;
        Succeeded = succeeded;
        Code = code;
        Message = message;
    }

    public static FunctionResult<T> Ok(T value) => new(value, true, string.Empty, string.Empty);

    public static FunctionResult<T> Fail(string code, string message) => new(default, false, code, message);
}
