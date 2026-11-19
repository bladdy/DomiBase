using DomiBase.Application.Runtime;

namespace DomiBase.Api.Http;

/// <summary>
/// Resolves the public API key before rate limiting. A valid key becomes a project-scoped
/// partition; missing or invalid keys fall back to the client address with defaults.
/// </summary>
public sealed class RuntimeResolutionMiddleware(RequestDelegate next)
{
    private static readonly string[] RuntimePrefixes =
    [
        "/rest/v1",
        "/storage/v1",
        "/functions/v1",
        "/auth/v1",
        "/realtime/v1"
    ];

    public async Task InvokeAsync(HttpContext context, IRuntimeProjectResolver resolver)
    {
        if (RuntimePrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
        {
            var apiKey = ReadApiKey(context);
            var result = string.IsNullOrWhiteSpace(apiKey)
                ? RuntimeResolveResult.InvalidKey()
                : await resolver.ResolveAsync(apiKey, context.RequestAborted);

            context.Items[RuntimeRequestResolution.HttpItemKey] =
                new RuntimeRequestResolution(apiKey, result);
        }

        await next(context);
    }

    internal static string? ReadApiKey(HttpContext context)
    {
        var header = context.Request.Headers["apikey"].ToString().Trim();
        if (!string.IsNullOrWhiteSpace(header))
        {
            return header;
        }

        if (context.Request.Query.TryGetValue("apikey", out var query) &&
            query.Count > 0 &&
            query[0] is { } value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return null;
    }
}

/// <summary>Shared access to the per-request resolution for endpoints and the rate limiter.</summary>
public static class RuntimeRequestContext
{
    public static async Task<RuntimeRequestResolution> GetOrResolveAsync(
        HttpContext context,
        IRuntimeProjectResolver resolver,
        CancellationToken cancellationToken = default)
    {
        if (context.Items.TryGetValue(RuntimeRequestResolution.HttpItemKey, out var stored) &&
            stored is RuntimeRequestResolution resolution)
        {
            return resolution;
        }

        var apiKey = RuntimeResolutionMiddleware.ReadApiKey(context);
        var result = string.IsNullOrWhiteSpace(apiKey)
            ? RuntimeResolveResult.InvalidKey()
            : await resolver.ResolveAsync(apiKey, cancellationToken);

        return new RuntimeRequestResolution(apiKey, result);
    }

    public static async Task<RuntimeProjectContext> RequireContextAsync(
        HttpContext context,
        IRuntimeProjectResolver resolver,
        CancellationToken cancellationToken = default)
    {
        var resolution = await GetOrResolveAsync(context, resolver, cancellationToken);

        if (string.IsNullOrWhiteSpace(resolution.ApiKey))
        {
            throw new RuntimeQueryException(
                401,
                "MISSING_API_KEY",
                "An API key is required.",
                "Send the public key in the 'apikey' header.");
        }

        switch (resolution.Result.Status)
        {
            case RuntimeResolveStatus.Success when resolution.Result.Context is not null:
                await resolver.TouchAsync(resolution.Result.Context.ApiKeyId, cancellationToken);
                return resolution.Result.Context;
            case RuntimeResolveStatus.InvalidKey:
                throw new RuntimeQueryException(
                    401,
                    "INVALID_API_KEY",
                    "The API key is invalid or has been revoked.");
            case RuntimeResolveStatus.ProjectNotReady:
                throw new RuntimeQueryException(
                    403,
                    "PROJECT_NOT_READY",
                    "The project is not ready to serve data requests.");
            default:
                throw new RuntimeQueryException(
                    503,
                    "PROJECT_UNAVAILABLE",
                    "The project is temporarily unavailable.");
        }
    }
}
