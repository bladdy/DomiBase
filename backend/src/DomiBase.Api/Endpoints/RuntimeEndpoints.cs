using System.Globalization;
using System.Text.Json;
using DomiBase.Api.Http;
using DomiBase.Application.Auth;
using DomiBase.Application.Runtime;
using DomiBase.Infrastructure.Auth;
using DomiBase.Infrastructure.Runtime;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Runtime Plane data API. The project is resolved server-side from the public API key;
/// clients never send a project id, database name, role or SQL.
/// </summary>
public static class RuntimeEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/rest/v1")
            .WithTags("Runtime")
            .RequireRateLimiting("runtime");

        group.MapGet("/{table}", SelectAsync).WithName("RuntimeSelect");
        group.MapPost("/{table}", InsertAsync).WithName("RuntimeInsert");
        group.MapPatch("/{table}", UpdateAsync).WithName("RuntimeUpdate");
        group.MapDelete("/{table}", DeleteAsync).WithName("RuntimeDelete");

        return app;
    }

    private static async Task<IResult> SelectAsync(
        string table,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        IRuntimeDataService data,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);
        var options = ParseReadOptions(http.Request);

        var result = await data.SelectAsync(context, table, options, user, cancellationToken);
        return DataResult(result, StatusCodes.Status200OK);
    }

    private static async Task<IResult> InsertAsync(
        string table,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        IRuntimeDataService data,
        RuntimeOptions options,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        var body = await ReadJsonAsync(http.Request, cancellationToken);
        var rows = RuntimeJsonValues.ReadMany(body);

        if (rows.Count > options.MaxInsertRows)
        {
            throw new RuntimeQueryException(
                413,
                "PAYLOAD_TOO_LARGE",
                $"At most {options.MaxInsertRows} rows can be inserted per request.");
        }

        var result = await data.InsertAsync(
            context,
            table,
            rows,
            ParseSelect(http.Request),
            user,
            cancellationToken);

        return DataResult(result, StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        string table,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        IRuntimeDataService data,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        var body = await ReadJsonAsync(http.Request, cancellationToken);
        var values = RuntimeJsonValues.ReadObject(body);
        var options = new RuntimeWriteOptions(ParseSelect(http.Request), ParseFilters(http.Request));

        var result = await data.UpdateAsync(context, table, values, options, user, cancellationToken);
        return DataResult(result, StatusCodes.Status200OK);
    }

    private static async Task<IResult> DeleteAsync(
        string table,
        HttpContext http,
        IRuntimeProjectResolver resolver,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        IRuntimeDataService data,
        CancellationToken cancellationToken)
    {
        var context = await RuntimeRequestContext.RequireContextAsync(http, resolver, cancellationToken);
        var user = await ResolveUserAsync(http, context, signingKeys, accessTokens, cancellationToken);

        var options = new RuntimeWriteOptions(ParseSelect(http.Request), ParseFilters(http.Request));

        var result = await data.DeleteAsync(context, table, options, user, cancellationToken);
        return DataResult(result, StatusCodes.Status200OK);
    }

    private static async Task<AccessTokenClaims?> ResolveUserAsync(
        HttpContext http,
        RuntimeProjectContext context,
        IAuthSigningKeyService signingKeys,
        IAccessTokenService accessTokens,
        CancellationToken cancellationToken)
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
            throw new RuntimeQueryException(
                401,
                "INVALID_TOKEN",
                "The bearer token is invalid or expired.");
        }

        var validation = accessTokens.Validate(keyMaterial, token);
        if (!validation.Succeeded || validation.Claims is null)
        {
            throw new RuntimeQueryException(
                401,
                "INVALID_TOKEN",
                "The bearer token is invalid or expired.");
        }

        var claims = validation.Claims;
        if (claims.ProjectId != context.ProjectId ||
            claims.ApiKeyId != context.ApiKeyId ||
            !string.Equals(claims.Role, AccessTokenClaims.AuthenticatedRole, StringComparison.Ordinal))
        {
            throw new RuntimeQueryException(
                401,
                "INVALID_TOKEN",
                "The bearer token does not belong to this project.");
        }

        return claims;
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                request.Body,
                cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new RuntimeQueryException(
                400,
                "INVALID_BODY",
                "The request body is not valid JSON.");
        }
    }

    private static RuntimeReadOptions ParseReadOptions(HttpRequest request)
    {
        var select = ParseSelect(request);
        var filters = ParseFilters(request);
        var order = request.Query["order"].ToString();
        var limit = ParseOptionalInt(request, "limit");
        var offset = ParseOptionalInt(request, "offset");

        return new RuntimeReadOptions(
            select,
            filters,
            string.IsNullOrWhiteSpace(order) ? null : order,
            limit,
            offset);
    }

    private static IReadOnlyList<string>? ParseSelect(HttpRequest request)
    {
        var raw = request.Query["select"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var columns = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return columns.Count == 0 ? null : columns;
    }

    private static IReadOnlyList<string> ParseFilters(HttpRequest request) =>
        request.Query["f"]
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();

    private static int? ParseOptionalInt(HttpRequest request, string name)
    {
        var raw = request.Query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new RuntimeQueryException(
                400,
                name == "limit" ? "INVALID_LIMIT" : "INVALID_OFFSET",
                $"{name} must be an integer.");
        }

        return value;
    }

    private static IResult DataResult(RuntimeResultSet result, int statusCode) =>
        Results.Json(
            new Dictionary<string, object?>
            {
                ["data"] = result.Rows,
                ["count"] = result.Affected
            },
            statusCode: statusCode);
}
