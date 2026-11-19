using DomiBase.Api.Http;
using DomiBase.Application.Extensions;
using DomiBase.Application.Projects;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Control Plane endpoints for the allowlisted PostgreSQL extension catalog. Protected by the
/// administrative middleware and never mapped under <c>/rest/v1</c>. Clients send only an
/// allowlisted extension name; SQL, versions, schemas and roles are never accepted.
/// </summary>
public static class ExtensionControlEndpoints
{
    public static IEndpointRouteBuilder MapExtensionControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/projects/{projectId:guid}/extensions", async (
                Guid projectId,
                ExtensionService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ListAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(result, extensions => Results.Ok(extensions.Select(ExtensionResponse.From)));
            })
            .WithName("ControlListProjectExtensions");

        app.MapPost("/api/v1/projects/{projectId:guid}/extensions", async (
                Guid projectId,
                ExtensionRequest? request,
                ExtensionService service,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request?.Name))
                {
                    return Invalid("A non-empty 'name' from the extension catalog is required.");
                }

                var result = await service.InstallAsync(projectId, request.Name, cancellationToken);
                return ControlResultMapper.ToHttp(result, mutation => Results.Ok(ExtensionMutationResponse.From(mutation)));
            })
            .WithName("ControlInstallProjectExtension");

        app.MapDelete("/api/v1/projects/{projectId:guid}/extensions/{name}", async (
                Guid projectId,
                string name,
                ExtensionService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.UninstallAsync(projectId, name, cancellationToken);
                return ControlResultMapper.ToHttp(result, mutation => Results.Ok(ExtensionMutationResponse.From(mutation)));
            })
            .WithName("ControlUninstallProjectExtension");

        return app;
    }

    private static IResult Invalid(string hint) =>
        ControlResultMapper.ToHttp(
            ControlResult<ExtensionMutationResult>.Fail(
                ControlErrorCode.Validation,
                "The extension request is not valid.",
                hint),
            _ => Results.Ok());

    private sealed record ExtensionRequest(string? Name);
}

public sealed record ExtensionResponse(
    string Name,
    string Description,
    bool Trusted,
    string? AvailableVersion,
    string? InstalledVersion,
    bool Available,
    bool Installed,
    bool CanInstall,
    bool CanUninstall)
{
    public static ExtensionResponse From(PostgresExtension extension) =>
        new(
            extension.Name,
            extension.Description,
            extension.Trusted,
            extension.AvailableVersion,
            extension.InstalledVersion,
            extension.Available,
            extension.Installed,
            extension.Available && !extension.Installed,
            extension.Installed);
}

public sealed record ExtensionMutationResponse(string Name, string Action)
{
    public static ExtensionMutationResponse From(ExtensionMutationResult result) =>
        new(result.Name, result.Action);
}
