using DomiBase.Api.Contracts;
using DomiBase.Api.Http;
using DomiBase.Application.Projects;
using DomiBase.Infrastructure.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Control Plane endpoints. Every route is protected by
/// <see cref="Security.AdminAuthenticationMiddleware"/>. Administrative routes
/// are never mapped under <c>/rest/v1</c>.
/// </summary>
public static class ControlEndpoints
{
    public static IEndpointRouteBuilder MapControlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").WithTags("Control");

        group.MapGet("/projects", async (
                ProjectService service,
                RuntimeOptions runtimeOptions,
                CancellationToken cancellationToken) =>
            {
                var projects = await service.ListAsync(cancellationToken);
                return Results.Ok(projects.Select(project => ProjectResponse.From(project, runtimeOptions)));
            })
            .WithName("ControlListProjects");

        group.MapGet("/projects/{id:guid}", async (
                Guid id,
                ProjectService service,
                RuntimeOptions runtimeOptions,
                CancellationToken cancellationToken) =>
            {
                var project = await service.GetAsync(id, cancellationToken);
                return project is null
                    ? ApiError.Create(StatusCodes.Status404NotFound, "NOT_FOUND", $"Project '{id}' was not found.")
                    : Results.Ok(ProjectResponse.From(project, runtimeOptions));
            })
            .WithName("ControlGetProject");

        group.MapPost("/projects", async (
                CreateProjectRequest request,
                ProjectService service,
                RuntimeOptions runtimeOptions,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(request.Slug, request.Name, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    project => Results.Created(
                        $"/api/v1/projects/{project.Id}",
                        ProjectResponse.From(project, runtimeOptions)));
            })
            .WithName("ControlCreateProject");

        group.MapDelete("/projects/{id:guid}", async (
                Guid id,
                HttpContext http,
                [FromBody] DeleteProjectRequest? request,
                ProjectService service,
                CancellationToken cancellationToken) =>
            {
                var confirmation = http.Request.Headers[ProjectService.DeleteConfirmationHeader].ToString();
                var result = await service.DeleteAsync(id, request?.ConfirmSlug, confirmation, cancellationToken);
                return ControlResultMapper.ToHttp(result, _ => Results.NoContent());
            })
            .WithName("ControlDeleteProject");

        // Required by the idempotent/retryable provisioning design: retries a Failed project.
        group.MapPost("/projects/{id:guid}/provision", async (
                Guid id,
                ProjectService service,
                RuntimeOptions runtimeOptions,
                CancellationToken cancellationToken) =>
            {
                var result = await service.RetryProvisioningAsync(id, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    project => Results.Ok(ProjectResponse.From(project, runtimeOptions)));
            })
            .WithName("ControlRetryProvisioning");

        group.MapPatch("/projects/{id:guid}/rate-limit", async (
                Guid id,
                UpdateRateLimitRequest request,
                ProjectService service,
                RuntimeOptions runtimeOptions,
                CancellationToken cancellationToken) =>
            {
                var result = await service.UpdateRateLimitAsync(
                    id,
                    request.Permits,
                    request.WindowSeconds,
                    cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    project => Results.Ok(ProjectResponse.From(project, runtimeOptions)));
            })
            .WithName("ControlUpdateProjectRateLimit");

        group.MapGet("/projects/{id:guid}/keys", async (
                Guid id,
                ProjectService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ListApiKeysAsync(id, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    keys => Results.Ok(keys.Select(ApiKeyResponse.From)));
            })
            .WithName("ControlListProjectKeys");

        group.MapPost("/projects/{id:guid}/keys", async (
                Guid id,
                CreateApiKeyRequest? request,
                ProjectService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateApiKeyAsync(
                    id,
                    request?.Kind,
                    request?.Label,
                    request?.ExpiresAt,
                    cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    created => Results.Created(
                        $"/api/v1/projects/{id}/keys/{created.Key.Id}",
                        CreatedApiKeyResponse.From(created)));
            })
            .WithName("ControlCreateProjectKey");

        group.MapPost("/projects/{id:guid}/keys/{keyId:guid}/rotate", async (
                Guid id,
                Guid keyId,
                RotateApiKeyRequest? request,
                ProjectService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.RotateApiKeyAsync(
                    id,
                    keyId,
                    request?.OverlapMinutes,
                    cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    created => Results.Created(
                        $"/api/v1/projects/{id}/keys/{created.Key.Id}",
                        CreatedApiKeyResponse.From(created)));
            })
            .WithName("ControlRotateProjectKey");

        group.MapDelete("/projects/{id:guid}/keys/{keyId:guid}", async (
                Guid id,
                Guid keyId,
                ProjectService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.RevokeApiKeyAsync(id, keyId, cancellationToken);
                return ControlResultMapper.ToHttp(result, _ => Results.NoContent());
            })
            .WithName("ControlRevokeProjectKey");

        return app;
    }
}
