using DomiBase.Application.Projects;
using DomiBase.Domain.Projects;

namespace DomiBase.Application.Schema;

/// <summary>
/// Shared Control Plane guard for operations that target a project database. Resolves the
/// project from trusted metadata, requires <c>Ready</c> and derives PostgreSQL resource names
/// before any privileged connection is opened.
/// </summary>
public sealed class ProjectSchemaResolver(IProjectRepository repository)
{
    public async Task<(ProjectResourceNames? Names, ControlError? Error)> ResolveAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        if (project is null)
        {
            return (null, new ControlError(
                ControlErrorCode.NotFound,
                $"Project '{projectId}' was not found."));
        }

        if (project.Status != ProjectStatus.Ready)
        {
            return (null, new ControlError(
                ControlErrorCode.Conflict,
                "The project database is not ready. This operation requires status Ready.",
                "Retry once provisioning has completed."));
        }

        if (!ProjectResourceNaming.TryResolve(project, out var names, out var namingError))
        {
            return (null, new ControlError(
                ControlErrorCode.Validation,
                namingError ?? "Invalid project metadata."));
        }

        return (names, null);
    }
}
