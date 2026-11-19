using DomiBase.Api.Http;
using DomiBase.Application.Security;

namespace DomiBase.Api.Security;

/// <summary>
/// Protects every <c>/api/v1</c> route with either the long-lived service token
/// or a short-lived signed panel session. The Runtime Plane (<c>/rest/v1</c>) and
/// system endpoints are never touched here.
/// </summary>
public sealed class AdminAuthenticationMiddleware(
    RequestDelegate next,
    AdminTokenValidator validator,
    AdminSessionService sessions,
    ILogger<AdminAuthenticationMiddleware> logger)
{
    public const string HeaderName = "X-DomiBase-Admin-Token";

    private const string PathPrefix = "/api/v1";
    private const string BearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var candidate = ExtractToken(context.Request);
        if (!validator.IsValid(candidate) && !sessions.IsValid(candidate))
        {
            logger.LogWarning(
                "Rejected unauthenticated Control Plane request {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);

            await ApiError.Create(
                    StatusCodes.Status401Unauthorized,
                    "UNAUTHORIZED",
                    "A valid administrative credential is required.",
                    hint: $"Sign in at /admin/v1/login and send the session token in the {HeaderName} header.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private static string? ExtractToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var headerValues))
        {
            var headerValue = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(headerValue))
            {
                return headerValue;
            }
        }

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var bearerToken = authorization[BearerPrefix.Length..].Trim();
            return string.IsNullOrWhiteSpace(bearerToken) ? null : bearerToken;
        }

        return null;
    }
}
