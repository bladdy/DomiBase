using System.Net.Mime;
using DomiBase.Api.Http;
using DomiBase.Application.Auth;
using DomiBase.Application.Runtime;

namespace DomiBase.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/auth/v1")
            .WithTags("Auth Runtime")
            .RequireRateLimiting("runtime");

        group.MapPost("/register", RegisterAsync)
            .Accepts<RegisterRequest>(MediaTypeNames.Application.Json)
            .Produces(StatusCodes.Status200OK, typeof(AuthResponse))
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/login", LoginAsync)
            .Accepts<LoginRequest>(MediaTypeNames.Application.Json)
            .Produces(StatusCodes.Status200OK, typeof(AuthResponse))
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/refresh", RefreshAsync)
            .Accepts<RefreshRequest>(MediaTypeNames.Application.Json)
            .Produces(StatusCodes.Status200OK, typeof(AuthResponse))
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", LogoutAsync)
            .Accepts<LogoutRequest>(MediaTypeNames.Application.Json)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext http,
        RegisterRequest request,
        IRuntimeProjectResolver resolver,
        IAuthService auth,
        CancellationToken cancellationToken)
    {
        var (context, status) = await ResolveContextAsync(http, resolver, cancellationToken);
        if (context is null)
        {
            return AuthErrorResult(status);
        }

        var result = await auth.RegisterAsync(context, request.Email, request.Password, cancellationToken);
        return MapResult(result);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        LoginRequest request,
        IRuntimeProjectResolver resolver,
        IAuthService auth,
        CancellationToken cancellationToken)
    {
        var (context, status) = await ResolveContextAsync(http, resolver, cancellationToken);
        if (context is null)
        {
            return AuthErrorResult(status);
        }

        var result = await auth.LoginAsync(context, request.Email, request.Password, cancellationToken);
        return MapResult(result);
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext http,
        RefreshRequest request,
        IRuntimeProjectResolver resolver,
        IAuthService auth,
        CancellationToken cancellationToken)
    {
        var (context, status) = await ResolveContextAsync(http, resolver, cancellationToken);
        if (context is null)
        {
            return AuthErrorResult(status);
        }

        var result = await auth.RefreshAsync(context, request.refreshToken, cancellationToken);
        return MapResult(result);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext http,
        LogoutRequest request,
        IRuntimeProjectResolver resolver,
        IAuthService auth,
        CancellationToken cancellationToken)
    {
        var (context, status) = await ResolveContextAsync(http, resolver, cancellationToken);
        if (context is null)
        {
            return AuthErrorResult(status);
        }

        var result = await auth.LogoutAsync(context, request.refreshToken, cancellationToken);
        if (result.Error != AuthError.None)
        {
            return MapResult(result);
        }

        return Results.Ok(new { data = (object?)null });
    }

    private static async Task<(RuntimeProjectContext? Context, RuntimeResolveStatus Status)> ResolveContextAsync(
        HttpContext http,
        IRuntimeProjectResolver resolver,
        CancellationToken cancellationToken)
    {
        var resolution = await RuntimeRequestContext.GetOrResolveAsync(http, resolver, cancellationToken);
        if (string.IsNullOrWhiteSpace(resolution.ApiKey) ||
            resolution.Result.Status != RuntimeResolveStatus.Success ||
            resolution.Result.Context is null)
        {
            return (null, resolution.Result.Status);
        }

        await resolver.TouchAsync(resolution.Result.Context.ApiKeyId, cancellationToken);
        return (resolution.Result.Context, resolution.Result.Status);
    }

    private static IResult AuthErrorResult(RuntimeResolveStatus status) =>
        ApiError.Create(
            StatusCodes.Status401Unauthorized,
            "INVALID_API_KEY",
            status == RuntimeResolveStatus.ProjectNotReady
                ? "The project is not ready."
                : "The apikey header is missing, revoked or invalid.");

    private static IResult MapResult(AuthResult result)
    {
        if (result.Error == AuthError.None && result.Session is not null)
        {
            return Results.Ok(new
            {
                data = new AuthResponse(
                    result.Session.AccessToken,
                    result.Session.AccessTokenExpiresAt,
                    result.Session.RefreshToken,
                    result.Session.User is null
                        ? null
                        : new AuthUserResponse(result.Session.User.Id, result.Session.User.Email, result.Session.User.CreatedAt))
            });
        }

        var (status, code) = result.Error switch
        {
            AuthError.InvalidInput => (StatusCodes.Status400BadRequest, "INVALID_INPUT"),
            AuthError.EmailAlreadyRegistered => (StatusCodes.Status409Conflict, "EMAIL_ALREADY_REGISTERED"),
            AuthError.InvalidCredentials => (StatusCodes.Status401Unauthorized, "INVALID_CREDENTIALS"),
            AuthError.InvalidRefreshToken => (StatusCodes.Status401Unauthorized, "INVALID_REFRESH_TOKEN"),
            _ => (StatusCodes.Status500InternalServerError, "UNAVAILABLE")
        };

        return ApiError.Create(status, code, result.Message ?? "Authentication failed.");
    }
}

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string refreshToken);

public sealed record LogoutRequest(string refreshToken);

public sealed record AuthUserResponse(Guid Id, string Email, DateTimeOffset CreatedAt);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    AuthUserResponse? User);
