using System.Text;
using System.Text.Json;
using DomiBase.Application.Functions;
using Wasmtime;

namespace DomiBase.Functions.Worker;

/// <summary>
/// Executes a guest WebAssembly module with an explicit capability-free ABI.
/// No WASI configuration is attached, imports are rejected, and CPU/memory/time
/// are bounded per invocation.
/// </summary>
internal static class WasmFunctionRunner
{
    private const int PageSize = 64 * 1024;
    private const int InputOffset = 1024;
    private const long MaxFuel = 10_000_000;
    private const int MaxMemoryPages = 256;
    private const int MaxTimeoutMs = 10_000;
    private const int MaxOutputBytes = 256 * 1024;

    public static FunctionWorkerResponse Run(FunctionWorkerRequest request)
    {
        var started = Environment.TickCount64;
        var limits = request.Limits;

        var invalidLimit = ValidateLimits(limits);
        if (invalidLimit is not null)
        {
            return Failure(invalidLimit.Value.Code, invalidLimit.Value.Message, started);
        }

        byte[] module;
        try
        {
            module = Convert.FromBase64String(request.ModuleBase64 ?? string.Empty);
        }
        catch (FormatException)
        {
            return Failure("INVALID_MODULE", "The module is not valid base64.", started);
        }

        if (module.Length == 0)
        {
            return Failure("INVALID_MODULE", "The module is empty.", started);
        }

        byte[] guestRequest;
        try
        {
            guestRequest = BuildGuestRequest(request.InputJson, request.ProjectId, request.UserId);
        }
        catch (JsonException)
        {
            return Failure("INVALID_INPUT", "The function input is not valid JSON.", started);
        }

        if (InputOffset + (long)guestRequest.Length > (long)limits.MemoryPages * PageSize)
        {
            return Failure("INPUT_TOO_LARGE", "The function input does not fit in the configured memory.", started);
        }

        try
        {
            using var config = new Config();
            config.WithBulkMemory(true);
            config.WithFuelConsumption(true);
            config.WithEpochInterruption(true);
            // Intentionally no WASI configuration: the guest has no filesystem or network.
            using var engine = new Engine(config);
            using var wasmModule = Module.FromBytes(engine, "domibase-function", module);

            if (wasmModule.Imports.Count > 0)
            {
                return Failure("INVALID_MODULE", "The module must not import host functions.", started);
            }

            if (!wasmModule.Exports.Any(e => string.Equals(e.Name, "memory", StringComparison.Ordinal)))
            {
                return Failure("INVALID_MODULE", "The module must export a memory named 'memory'.", started);
            }

            if (!wasmModule.Exports.Any(e => string.Equals(e.Name, "invoke", StringComparison.Ordinal)))
            {
                return Failure("INVALID_MODULE", "The module must export a function named 'invoke'.", started);
            }

            using var store = new Store(engine);
            store.SetLimits(
                memorySize: (long)limits.MemoryPages * PageSize,
                tableElements: 1000U,
                instances: 1L,
                tables: 1L,
                memories: 1L);
            store.Fuel = (ulong)limits.Fuel;

            var instance = new Instance(store, wasmModule, Array.Empty<object>());
            var memory = instance.GetMemory("memory");
            if (memory is null)
            {
                return Failure("INVALID_MODULE", "The 'memory' export is not a linear memory.", started);
            }

            var invoke = instance.GetFunction("invoke", typeof(long), new[] { typeof(int), typeof(int) });
            if (invoke is null)
            {
                return Failure("INVALID_MODULE", "'invoke' must have the signature (i32, i32) -> i64.", started);
            }

            var requiredBytes = InputOffset + (long)guestRequest.Length;
            if (memory.GetLength() < requiredBytes)
            {
                var requiredPages = (requiredBytes + PageSize - 1) / PageSize;
                var currentPages = memory.GetLength() / PageSize;
                if (requiredPages > limits.MemoryPages)
                {
                    return Failure("INPUT_TOO_LARGE", "The function input does not fit in the configured memory.", started);
                }

                memory.Grow(requiredPages - currentPages);
            }

            guestRequest.CopyTo(memory.GetSpan(InputOffset, guestRequest.Length));

            // Epoch interruption bounds only the guest call: module compilation and
            // process startup must not consume the function's time budget.
            var epochInterval = Math.Max(1, limits.TimeoutMs / 10);
            using var epochTimer = new Timer(_ => engine.IncrementEpoch(), null, epochInterval, epochInterval);
            store.SetEpochDeadline(2);

            var packed = Convert.ToInt64(invoke.Invoke(InputOffset, guestRequest.Length));
            var unpacked = unchecked((ulong)packed);
            var offset = (long)(unpacked >> 32);
            var length = (long)(unpacked & 0xFFFFFFFFUL);
            var memoryLength = memory.GetLength();

            if (offset > memoryLength || length > memoryLength - offset)
            {
                return Failure("INVALID_RESULT", "The module returned a pointer outside its memory.", started);
            }

            if (length > limits.MaxOutputBytes)
            {
                return Failure("OUTPUT_TOO_LARGE", "The function output exceeds the configured limit.", started);
            }

            var output = memory.GetSpan(offset, (int)length).ToArray();

            // The guest result must be a single JSON value. Validate before returning
            // so the API can safely embed it in the canonical { data } envelope.
            using var document = JsonDocument.Parse(output);

            return new FunctionWorkerResponse
            {
                OutputBase64 = Convert.ToBase64String(output),
                FuelConsumed = limits.Fuel - (long)store.Fuel,
                DurationMs = Environment.TickCount64 - started
            };
        }
        catch (TrapException exception)
        {
            var trap = exception.Message;
            if (trap.Contains("fuel", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("FUNCTION_OUT_OF_FUEL", "The function exceeded its CPU budget.", started);
            }

            if (trap.Contains("interrupt", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("FUNCTION_TIMEOUT", "The function exceeded its time limit.", started);
            }

            if (trap.Contains("out of bounds", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("FUNCTION_TRAP", "The function accessed memory outside its bounds.", started);
            }

            return Failure("FUNCTION_TRAP", "The function trapped during execution.", started);
        }
        catch (WasmtimeException)
        {
            return Failure("INVALID_MODULE", "The module could not be instantiated.", started);
        }
        catch (JsonException)
        {
            return Failure("INVALID_RESULT", "The function did not return valid JSON.", started);
        }
        catch (OutOfMemoryException)
        {
            return Failure("MEMORY_LIMIT_EXCEEDED", "The function exceeded its memory limit.", started);
        }
        catch (Exception)
        {
            return Failure("WORKER_ERROR", "The function could not be executed.", started);
        }
    }

    private static byte[] BuildGuestRequest(string inputJson, string projectId, string? userId)
    {
        using var input = JsonDocument.Parse(inputJson ?? "null");
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("input");
            input.RootElement.WriteTo(writer);
            writer.WriteStartObject("context");
            writer.WriteString("projectId", projectId);
            if (userId is null)
            {
                writer.WriteNull("userId");
            }
            else
            {
                writer.WriteString("userId", userId);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static (string Code, string Message)? ValidateLimits(FunctionWorkerLimits limits)
    {
        if (limits.Fuel <= 0 || limits.Fuel > MaxFuel)
        {
            return ("INVALID_LIMITS", $"fuel must be between 1 and {MaxFuel}.");
        }

        if (limits.MemoryPages <= 0 || limits.MemoryPages > MaxMemoryPages)
        {
            return ("INVALID_LIMITS", $"memoryPages must be between 1 and {MaxMemoryPages}.");
        }

        if (limits.TimeoutMs <= 0 || limits.TimeoutMs > MaxTimeoutMs)
        {
            return ("INVALID_LIMITS", $"timeoutMs must be between 1 and {MaxTimeoutMs}.");
        }

        if (limits.MaxOutputBytes <= 0 || limits.MaxOutputBytes > MaxOutputBytes)
        {
            return ("INVALID_LIMITS", $"maxOutputBytes must be between 1 and {MaxOutputBytes}.");
        }

        return null;
    }

    private static FunctionWorkerResponse Failure(string code, string message, long started) =>
        new()
        {
            Error = new FunctionWorkerError { Code = code, Message = message },
            DurationMs = Environment.TickCount64 - started
        };
}
