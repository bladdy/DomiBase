using DomiBase.Application.Auditing;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Backups;
using DomiBase.Domain.Projects;

namespace DomiBase.Application.Projects;

/// <summary>
/// Persistence contract for Control Plane metadata. Implemented with EF Core over
/// <c>domibase_control</c>. Project data databases are never accessed here.
/// </summary>
public interface IProjectRepository
{
    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Adds the project. Returns <c>false</c> on a unique constraint conflict.</summary>
    Task<bool> TryAddProjectAsync(Project project, CancellationToken cancellationToken = default);

    Task<Project?> FindProjectAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default);

    void AddProvisioningOperations(IEnumerable<ProvisioningOperation> operations);

    /// <summary>Persists a step as <see cref="ProvisioningStatus.Running"/> before privileged SQL runs.</summary>
    Task<ProvisioningOperation> AddProvisioningOperationAsync(
        ProvisioningOperation operation,
        CancellationToken cancellationToken = default);

    Task CompleteProvisioningOperationAsync(
        Guid id,
        ProvisioningStatus status,
        string? error,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task<int> NextProvisioningAttemptAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProvisioningOperation>> ListProvisioningOperationsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every control-database row belonging to the project. Audit entries are kept:
    /// they are keyed by target id and are not foreign-keyed to the project.
    /// </summary>
    Task DeleteProjectMetadataAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task AddBackupAsync(ProjectBackup backup, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectBackup>> ListBackupsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<ProjectBackup?> FindBackupAsync(Guid projectId, Guid backupId, CancellationToken cancellationToken = default);

    Task RemoveBackupAsync(ProjectBackup backup, CancellationToken cancellationToken = default);

    /// <summary>Sets the transitional/final status used by a logical restore.</summary>
    Task UpdateProjectStatusAsync(
        Guid projectId,
        ProjectStatus status,
        string? failureReason,
        CancellationToken cancellationToken = default);

    /// <summary>Adds the key. Returns <c>false</c> on a unique constraint conflict.</summary>
    Task<bool> TryAddApiKeyAsync(ApiKey apiKey, CancellationToken cancellationToken = default);

    Task<ApiKey?> FindApiKeyAsync(Guid projectId, Guid keyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Read-only, bounded audit query over the control database.</summary>
    Task<AuditPage> ListAuditEntriesAsync(AuditQuery query, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
