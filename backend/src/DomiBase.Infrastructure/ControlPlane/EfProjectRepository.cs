using DomiBase.Application.Auditing;
using DomiBase.Application.Projects;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Backups;
using DomiBase.Domain.Projects;
using DomiBase.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DomiBase.Infrastructure.ControlPlane;

/// <summary>
/// EF Core implementation of the Control Plane metadata repository.
/// Only <c>domibase_control</c> is touched; project databases are handled by provisioning.
/// </summary>
public sealed class EfProjectRepository(DomiBaseDbContext db) : IProjectRepository
{
    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
        db.Projects.AnyAsync(p => p.Slug == slug, cancellationToken);

    public async Task<bool> TryAddProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        db.Projects.Add(project);
        return await TrySaveUniqueAsync(project, cancellationToken);
    }

    public Task<Project?> FindProjectAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Projects.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        await db.Projects
            .AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

    public void AddProvisioningOperations(IEnumerable<ProvisioningOperation> operations) =>
        db.ProvisioningOperations.AddRange(operations);

    public async Task<ProvisioningOperation> AddProvisioningOperationAsync(
        ProvisioningOperation operation,
        CancellationToken cancellationToken = default)
    {
        db.ProvisioningOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        return operation;
    }

    public Task CompleteProvisioningOperationAsync(
        Guid id,
        ProvisioningStatus status,
        string? error,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) =>
        db.ProvisioningOperations
            .Where(operation => operation.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(operation => operation.Status, status)
                    .SetProperty(operation => operation.Error, error)
                    .SetProperty(operation => operation.CompletedAt, completedAt),
                cancellationToken);

    public async Task<int> NextProvisioningAttemptAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.ProvisioningOperations.CountAsync(operation => operation.ProjectId == projectId, cancellationToken) + 1;

    public async Task<IReadOnlyList<ProvisioningOperation>> ListProvisioningOperationsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.ProvisioningOperations
            .AsNoTracking()
            .Where(operation => operation.ProjectId == projectId)
            .OrderBy(operation => operation.StartedAt)
            .ToListAsync(cancellationToken);

    public async Task DeleteProjectMetadataAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        // Storage buckets and functions have no FK to projects, so they must be removed
        // explicitly. Storage objects go first to respect their bucket foreign key.
        await db.ProjectBackups
            .Where(backup => backup.ProjectId == projectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.StorageObjects
            .Where(obj => db.StorageBuckets.Any(bucket =>
                bucket.Id == obj.BucketId && bucket.ProjectId == projectId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.StorageBuckets
            .Where(bucket => bucket.ProjectId == projectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Functions
            .Where(function => function.ProjectId == projectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ProjectAuthKeys
            .Where(key => key.ProjectId == projectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ApiKeys
            .Where(key => key.ProjectId == projectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ProvisioningOperations
            .Where(operation => operation.ProjectId == projectId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Projects
            .Where(project => project.Id == projectId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task AddBackupAsync(ProjectBackup backup, CancellationToken cancellationToken = default)
    {
        db.ProjectBackups.Add(backup);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectBackup>> ListBackupsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.ProjectBackups
            .AsNoTracking()
            .Where(backup => backup.ProjectId == projectId)
            .OrderByDescending(backup => backup.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<ProjectBackup?> FindBackupAsync(
        Guid projectId,
        Guid backupId,
        CancellationToken cancellationToken = default) =>
        db.ProjectBackups
            .AsNoTracking()
            .FirstOrDefaultAsync(
                backup => backup.ProjectId == projectId && backup.Id == backupId,
                cancellationToken);

    public async Task RemoveBackupAsync(ProjectBackup backup, CancellationToken cancellationToken = default)
    {
        db.ProjectBackups.Remove(backup);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task UpdateProjectStatusAsync(
        Guid projectId,
        ProjectStatus status,
        string? failureReason,
        CancellationToken cancellationToken = default) =>
        db.Projects
            .Where(project => project.Id == projectId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(project => project.Status, status)
                    .SetProperty(project => project.FailureReason, failureReason)
                    .SetProperty(project => project.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);

    public async Task<bool> TryAddApiKeyAsync(ApiKey apiKey, CancellationToken cancellationToken = default)
    {
        db.ApiKeys.Add(apiKey);
        return await TrySaveUniqueAsync(apiKey, cancellationToken);
    }

    public Task<ApiKey?> FindApiKeyAsync(Guid projectId, Guid keyId, CancellationToken cancellationToken = default) =>
        db.ApiKeys.FirstOrDefaultAsync(k => k.ProjectId == projectId && k.Id == keyId, cancellationToken);

    public async Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await db.ApiKeys
            .AsNoTracking()
            .Where(k => k.ProjectId == projectId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        db.AuditEntries.Add(entry);
        return Task.CompletedTask;
    }

    public async Task<AuditPage> ListAuditEntriesAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        // EF translates every condition into a parameter; no SQL text is built from the query.
        var source = db.AuditEntries.AsNoTracking().AsQueryable();

        if (query.ProjectId is { } projectId)
        {
            source = source.Where(entry => entry.ProjectId == projectId);
        }
        if (query.Action is not null)
        {
            source = source.Where(entry => entry.Action == query.Action);
        }
        if (query.Actor is not null)
        {
            source = source.Where(entry => entry.Actor == query.Actor);
        }
        if (query.TargetType is not null)
        {
            source = source.Where(entry => entry.TargetType == query.TargetType);
        }
        if (query.From is { } from)
        {
            source = source.Where(entry => entry.OccurredAt >= from);
        }
        if (query.To is { } to)
        {
            source = source.Where(entry => entry.OccurredAt <= to);
        }

        var total = await source.CountAsync(cancellationToken);
        var items = await source
            .OrderByDescending(entry => entry.OccurredAt)
            .ThenByDescending(entry => entry.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);

        return new AuditPage(items, total);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);

    private async Task<bool> TrySaveUniqueAsync(object entity, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }
}
