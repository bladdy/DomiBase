using System.Diagnostics;
using System.Text;
using DomiBase.Application.Functions;
using Microsoft.Extensions.Logging;

namespace DomiBase.Infrastructure.Functions;

/// <summary>
/// Starts a fresh worker process per invocation. The API host never loads,
/// compiles or interprets a guest module itself.
/// </summary>
public sealed class ProcessIsolatedFunctionExecutor(
    FunctionOptions options,
    ILogger<ProcessIsolatedFunctionExecutor> logger) : IFunctionExecutor
{
    private readonly SemaphoreSlim _concurrency = new(options.MaxConcurrency, options.MaxConcurrency);

    public async Task<FunctionExecutionResult> ExecuteAsync(
        FunctionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.WorkerPath))
        {
            logger.LogError("Function worker is not available at {WorkerPath}", options.WorkerPath);
            return FunctionExecutionResult.Fail(
                "FUNCTION_UNAVAILABLE",
                "The function runtime is temporarily unavailable.");
        }

        await _concurrency.WaitAsync(cancellationToken);

        Process? process = null;
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            var started = Environment.TickCount64;
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(options.WorkerPath);

            process = Process.Start(startInfo);
            if (process is null)
            {
                return FunctionExecutionResult.Fail(
                    "FUNCTION_UNAVAILABLE",
                    "The function runtime is temporarily unavailable.");
            }

            stdoutTask = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                ResponseLimit(request.Limits.MaxOutputBytes),
                cancellationToken);
            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var workerRequest = FunctionWorkerProtocol.SerializeRequest(new FunctionWorkerRequest
            {
                ModuleBase64 = Convert.ToBase64String(request.Module),
                InputJson = request.InputJson,
                ProjectId = request.ProjectId.ToString("D"),
                UserId = request.UserId,
                Limits = request.Limits
            });

            await process.StandardInput.WriteAsync(workerRequest.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.WorkerStartupTimeoutMs + request.Limits.TimeoutMs);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
                when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return FunctionExecutionResult.Fail(
                    "FUNCTION_TIMEOUT",
                    "The function exceeded its time limit.",
                    durationMs: Environment.TickCount64 - started);
            }

            var raw = await stdoutTask;
            var response = FunctionWorkerProtocol.ParseResponse(raw);
            var durationMs = response.DurationMs > 0
                ? response.DurationMs
                : Environment.TickCount64 - started;

            if (response.Error is not null)
            {
                return FunctionExecutionResult.Fail(
                    response.Error.Code,
                    response.Error.Message,
                    response.FuelConsumed,
                    durationMs);
            }

            if (process.ExitCode != 0 || string.IsNullOrEmpty(response.OutputBase64))
            {
                return FunctionExecutionResult.Fail(
                    "WORKER_ERROR",
                    "The function did not produce a valid response.",
                    response.FuelConsumed,
                    durationMs);
            }

            var outputJson = Encoding.UTF8.GetString(Convert.FromBase64String(response.OutputBase64));
            return FunctionExecutionResult.Ok(outputJson, response.FuelConsumed, durationMs);
        }
        catch (OperationCanceledException)
        {
            return FunctionExecutionResult.Fail("FUNCTION_ABORTED", "The invocation was cancelled.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Function worker invocation failed");
            return FunctionExecutionResult.Fail("WORKER_ERROR", "The function could not be executed.");
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                TryKill(process);
            }

            process?.Dispose();

            // Drain stderr so a blocked pipe cannot keep the process alive.
            if (stderrTask is not null)
            {
                try
                {
                    await stderrTask;
                }
                catch
                {
                    // Diagnostics are logged by the worker host; never forwarded to callers.
                }
            }

            _concurrency.Release();
        }
    }

    private static int ResponseLimit(int maxOutputBytes) =>
        Math.Min(512 * 1024, checked(maxOutputBytes * 2 + 8192));

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("The worker response exceeded the configured limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the check and the kill.
        }
    }
}
