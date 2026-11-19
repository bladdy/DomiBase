using System.Text.Json.Serialization;
using DomiBase.Api.Http;
using DomiBase.Application.Security;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Public entry point for the private panel. It verifies the single-owner
/// credentials and returns only a short-lived signed session token; the
/// permanent administrative service token is never sent to the browser.
/// </summary>
public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/v1")
            .WithTags("Admin Auth")
            .RequireRateLimiting("admin-login");

        group.MapPost("/login", LoginAsync)
            .WithName("AdminLogin")
            .Accepts<AdminLoginRequest>("application/json")
            .Produces<AdminLoginResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static IResult LoginAsync(AdminLoginRequest request, AdminSessionService sessions)
    {
        if (!sessions.TrySignIn(request.Username, request.Password, out var token, out var expiresAt))
        {
            return ApiError.Create(
                StatusCodes.Status401Unauthorized,
                "INVALID_CREDENTIALS",
                "Usuario o contraseña incorrectos.");
        }

        return Results.Ok(new AdminLoginResponse(token, expiresAt));
    }

    public sealed record AdminLoginRequest(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("password")] string Password);

    public sealed record AdminLoginResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
}
