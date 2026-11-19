using System.Text;
using System.Text.Json;
using DomiBase.Api.Http;
using DomiBase.Application.Auth;
using DomiBase.Application.Functions;
using DomiBase.Application.Runtime;
using DomiBase.Infrastructure.Auth;
using DomiBase.Infrastructure.Runtime;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Runtime Plane function API. The project is resolved server-side from the
/// public API key; callers never supply a project id or a function module.
/// </summary>
public static class FunctionRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapFunctionRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/functions/v1/{name}")
            .WithTags("Functions Runtime")
            .RequireRateLimiting("runtime")
            .MapPost("", InvokeAsync);

        return app;
    }

    private static async Task<IResult> InvokeAsync(
        string name,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        FunctionService functions,
        FunctionOptions options,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        string inputJson;
        try
        {
            inputJson = await ReadInputAsync(http.Request.Body, options.MaxInputBytes, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return ApiError.Create(
                StatusCodes.Status413PayloadTooLarge,
                "INPUT_TOO_LARGE",
                "The function input is too large.");
        }

        try
        {
            using var parsed = JsonDocument.Parse(inputJson);
        }
        catch (JsonException)
        {
            return ApiError.Create(StatusCodes.Status400BadRequest, "INVALID_INPUT", "The input must be valid JSON.");
        }

        var result = await functions.InvokeAsync(
            context.ProjectId,
            name,
            inputJson,
            user?.UserId.ToString("D"),
            cancellationToken);

        if (!result.Succeeded || result.Value is null)
        {
            return MapError(result.Code, result.Message);
        }

        using var output = JsonDocument.Parse(result.Value);
        return Results.Json(new { data = output.RootElement.Clone() });
    }

    private static async Task<string> ReadInputAsync(
        Stream body,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException();
            }

            buffer.Write(chunk, 0, read);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return string.IsNullOrWhiteSpace(text) ? "null" : text;
    }

    private static IResult MapError(string code, string message)
    {
        var status = code switch
        {
            "FUNCTION_NOT_FOUND" => StatusCodes.Status404NotFound,
            "FUNCTION_DISABLED" or "FORBIDDEN" => StatusCodes.Status403Forbidden,
            "INPUT_TOO_LARGE" or "OUTPUT_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
            "FUNCTION_TIMEOUT" => StatusCodes.Status408RequestTimeout,
            "FUNCTION_OUT_OF_FUEL" or "INVALID_INPUT" or "INVALID_FUNCTION_NAME"
                or "INVALID_LIMITS" => StatusCodes.Status422UnprocessableEntity,
            "FUNCTION_UNAVAILABLE" or "WORKER_ERROR" => StatusCodes.Status503ServiceUnavailable,
            "AUTH_REQUIRED" or "INVALID_TOKEN" or "INVALID_API_KEY" or "MISSING_API_KEY"
                => StatusCodes.Status401Unauthorized,
            "PROJECT_NOT_READY" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return ApiError.Create(status, code, message);
    }

    private static async Task<AccessTokenClaims?> ResolveUserAsync(
        HttpContext http,
        RuntimeProjectContext context,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        CancellationToken cancellationToken = default)
    {
        var authorization = http.Request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";

        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[bearerPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var keyMaterial = await signingKeys.FindActiveAsync(context.ProjectId, cancellationToken);
        if (keyMaterial is null)
        {
            throw new RuntimeQueryException(401, "INVALID_TOKEN", "The bearer token is invalid or expired.");
        }

        var validation = accessTokens.Validate(keyMaterial, token);
        if (!validation.Succeeded || validation.Claims is null)
        {
            throw new RuntimeQueryException(401, "INVALID_TOKEN", "The bearer token is invalid or expired.");
        }

        var claims = validation.Claims;
        if (claims.ProjectId != context.ProjectId ||
            claims.ApiKeyId != context.ApiKeyId ||
            !string.Equals(claims.Role, AccessTokenClaims.AuthenticatedRole, StringComparison.Ordinal))
        {
            throw new RuntimeQueryException(401, "INVALID_TOKEN", "The bearer token does not belong to this project.");
        }

        return claims;
    }
}
