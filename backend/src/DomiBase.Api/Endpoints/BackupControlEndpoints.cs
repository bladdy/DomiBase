using DomiBase.Api.Http;
using DomiBase.Application.Backups;

namespace DomiBase.Api.Endpoints;

/// <summary>
/// Control Plane endpoints for logical project backups. Protected by the administrative
/// middleware and never mapped under <c>/rest/v1</c>. Clients send no SQL, paths or credentials.
/// </summary>
public static class BackupControlEndpoints
{
    public static IEndpointRouteBuilder MapBackupControlEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/projects/{projectId:guid}/backups", async (
                Guid projectId,
                BackupService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.ListAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(result, backups => Results.Ok(backups));
            })
            .WithName("ControlListProjectBackups");

        app.MapPost("/api/v1/projects/{projectId:guid}/backups", async (
                Guid projectId,
                BackupService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(projectId, cancellationToken);
                return ControlResultMapper.ToHttp(
                    result,
                    backup => Results.Created($"/api/v1/projects/{projectId}/backups/{backup.Id}", backup));
            })
            .WithName("ControlCreateProjectBackup");

        app.MapGet("/api/v1/projects/{projectId:guid}/backups/{backupId:guid}/download", async (
                Guid projectId,
                Guid backupId,
                BackupService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.DownloadAsync(projectId, backupId, cancellationToken);
                if (!result.Succeeded || result.Value is null)
                {
                    return ControlResultMapper.ToHttp(result, _ => Results.Ok());
                }

                return Results.File(
                    result.Value.Content,
                    contentType: "application/octet-stream",
                    fileDownloadName: result.Value.FileName);
            })
            .WithName("ControlDownloadProjectBackup");

        app.MapDelete("/api/v1/projects/{projectId:guid}/backups/{backupId:guid}", async (
                Guid projectId,
                Guid backupId,
                BackupService service,
                CancellationToken cancellationToken) =>
            {
                var result = await service.DeleteAsync(projectId, backupId, cancellationToken);
                return ControlResultMapper.ToHttp(result, _ => Results.NoContent());
            })
            .WithName("ControlDeleteProjectBackup");

        app.MapPost("/api/v1/projects/{projectId:guid}/backups/{backupId:guid}/restore", async (
                Guid projectId,
                Guid backupId,
                HttpContext http,
                BackupService service,
                CancellationToken cancellationToken) =>
            {
                var confirmation = http.Request.Headers[BackupService.RestoreConfirmationHeader].ToString();
                var result = await service.RestoreAsync(projectId, backupId, confirmation, cancellationToken);
                return ControlResultMapper.ToHttp(result, restored => Results.Ok(restored));
            })
            .WithName("ControlRestoreProjectBackup");

        return app;
    }
}
