using System.Globalization;
using DomiBase.Api.Http;
using DomiBase.Application.Auth;
using DomiBase.Application.Runtime;
using DomiBase.Application.Storage;
using DomiBase.Infrastructure.Auth;
using DomiBase.Infrastructure.Runtime;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Runtime Plane storage API. The project is resolved server-side from the public API key.
/// </summary>
public static class StorageRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapStorageRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/storage/v1/{bucket}")
            .WithTags("Storage Runtime")
            .RequireRateLimiting("runtime");

        group.MapGet("", ListAsync);
        group.MapPost("/{*path}", UploadAsync);
        group.MapGet("/{*path}", DownloadAsync);
        group.MapDelete("/{*path}", DeleteAsync);
        group.MapPost("/signed/{*path}", CreateSignedUrlAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        string bucket,
        string? prefix,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        StorageService storage,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        var result = await storage.ListObjectsAsync(context.ProjectId, bucket, prefix, user is not null, cancellationToken);
        return MapResult(result, objects => Results.Json(new { data = objects.Select(ObjectResponse.From) }));
    }

    private static async Task<IResult> UploadAsync(
        string bucket,
        string path,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        StorageService storage,
        StorageOptions options,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        var contentLength = http.Request.ContentLength;
        if (contentLength is not null && contentLength > options.MaxObjectSizeBytes)
        {
            return ApiError.Create(
                StatusCodes.Status413PayloadTooLarge,
                "OBJECT_TOO_LARGE",
                $"Object exceeds the maximum size of {options.MaxObjectSizeBytes} bytes.");
        }

        var contentType = http.Request.ContentType;
        var result = await storage.UploadAsync(
            context.ProjectId,
            bucket,
            path,
            contentType,
            http.Request.Body,
            user is not null,
            cancellationToken);

        return MapResult(result, obj => Results.Json(new { data = ObjectResponse.From(obj) }));
    }

    private static async Task<IResult> DownloadAsync(
        string bucket,
        string path,
        long? expires,
        string? signature,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        StorageService storage,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);

        // A signed URL carries its own authorization; otherwise derive identity from the bearer token.
        var hasSignature = !string.IsNullOrWhiteSpace(signature) || expires.HasValue;
        if (hasSignature)
        {
            if (!expires.HasValue || string.IsNullOrWhiteSpace(signature))
            {
                return ApiError.Create(
                    StatusCodes.Status400BadRequest,
                    "INVALID_SIGNATURE",
                    "Both 'expires' and 'signature' are required.");
            }

            var validation = storage.ValidateSignedUrl(context.ProjectId, bucket, path, expires.Value, signature);
            if (!validation.Succeeded)
            {
                return MapError(validation);
            }
        }

        var user = hasSignature
            ? null
            : await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        // A valid signed URL was issued by the server for an authenticated user, so it grants read access.
        var result = await storage.DownloadAsync(
            context.ProjectId, bucket, path, hasSignature || user is not null, cancellationToken);

        if (!result.Succeeded || result.Value is null)
        {
            return MapError(result);
        }

        var obj = result.Object!;
        return Results.File(
            result.Value,
            contentType: obj.ContentType ?? "application/octet-stream",
            fileDownloadName: Path.GetFileName(path),
            lastModified: obj.UpdatedAt.UtcDateTime);
    }

    private static async Task<IResult> DeleteAsync(
        string bucket,
        string path,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        StorageService storage,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        var result = await storage.DeleteAsync(context.ProjectId, bucket, path, user is not null, cancellationToken);
        return MapResult(result, _ => Results.NoContent());
    }

    private static async Task<IResult> CreateSignedUrlAsync(
        string bucket,
        string path,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        StorageService storage,
        CancellationToken cancellationToken)
    {
        // Signed URLs require authentication; the signature itself does not carry identity.
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        if (user is null)
        {
            return ApiError.Create(
                StatusCodes.Status401Unauthorized,
                "AUTH_REQUIRED",
                "Signed URLs require an authenticated user.");
        }

        var expiresText = http.Request.Query["expiresIn"].ToString();
        if (!int.TryParse(expiresText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresSeconds) ||
            expiresSeconds <= 0)
        {
            expiresSeconds = 3600;
        }

        var result = storage.CreateSignedUrl(context.ProjectId, bucket, path, TimeSpan.FromSeconds(expiresSeconds));
        return MapResult(result, url => Results.Json(new { data = new { url } }));
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

    private static IResult MapResult<T>(StorageResult<T> result, Func<T, IResult> onSuccess)
    {
        if (!result.Succeeded || result.Value is null)
        {
            return MapError(result);
        }

        return onSuccess(result.Value);
    }

    private static IResult MapError<T>(StorageResult<T> result)
    {
        var status = result.Code switch
        {
            "BUCKET_NOT_FOUND" or "OBJECT_NOT_FOUND" => StatusCodes.Status404NotFound,
            "FORBIDDEN" => StatusCodes.Status403Forbidden,
            "OBJECT_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
            "SIGNED_URL_EXPIRED" => StatusCodes.Status410Gone,
            "INVALID_SIGNATURE" => StatusCodes.Status401Unauthorized,
            _ => StatusCodes.Status400BadRequest
        };

        return ApiError.Create(status, result.Code, result.Message);
    }
}

public sealed record ObjectResponse(
    Guid Id,
    string Path,
    string? ContentType,
    long Size,
    string Etag,
    DateTimeOffset CreatedAt)
{
    public static ObjectResponse From(Domain.Storage.StorageObject obj) => new(
        obj.Id,
        obj.Path,
        obj.ContentType,
        obj.Size,
        obj.Etag,
        obj.CreatedAt);
}
