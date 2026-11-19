using System.Text.Json;
using DomiBase.Application.Runtime;

namespace DomiBase.Api.Http;

/// <summary>
/// Converts unhandled exceptions into the canonical error envelope without
/// leaking stack traces, SQL or secrets to clients.
/// </summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (RuntimeQueryException exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            logger.LogInformation(
                "Runtime request rejected for {Method} {Path}: {Code} {Status}.",
                context.Request.Method,
                context.Request.Path,
                exception.Code,
                exception.StatusCode);

            context.Response.Clear();
            await ApiError.Create(
                    exception.StatusCode,
                    exception.Code,
                    exception.Message,
                    hint: exception.Hint)
                .ExecuteAsync(context);
        }
        catch (BadHttpRequestException exception)
        {
            logger.LogInformation(
                "Invalid request body for {Method} {Path}: {Reason}",
                context.Request.Method,
                context.Request.Path,
                exception.Message);

            if (context.Response.HasStarted)
            {
                throw;
            }

            var tooLarge = exception.StatusCode == StatusCodes.Status413PayloadTooLarge;
            context.Response.Clear();
            await ApiError.Create(
                    tooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest,
                    tooLarge ? "PAYLOAD_TOO_LARGE" : "VALIDATION_ERROR",
                    tooLarge ? "The request body is too large." : "The request body is invalid or missing.",
                    hint: tooLarge ? null : "Send a JSON object with the documented fields.")
                .ExecuteAsync(context);
        }
        catch (JsonException)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            await ApiError.Create(
                    StatusCodes.Status400BadRequest,
                    "INVALID_BODY",
                    "The request body is not valid JSON.")
                .ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected; nothing to write.
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            await ApiError.Create(
                    StatusCodes.Status500InternalServerError,
                    "INTERNAL_ERROR",
                    "An unexpected error occurred.")
                .ExecuteAsync(context);
        }
    }
}
