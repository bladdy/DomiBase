using DomiBase.Application.Auditing;
using DomiBase.Application.Backups;
using DomiBase.Application.Projects;
using DomiBase.Domain.Auditing;
using DomiBase.Domain.Backups;
using DomiBase.Domain.Projects;

namespace DomiBase.UnitTests;

internal sealed class FakeProjectRepository(Project project) : IProjectRepository
{
    public List<ProjectBackup> Backups { get; } = [];

    public List<AuditEntry> Audits { get; } = [];

    public List<ProjectStatus> StatusUpdates { get; } = [];

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
        Task.FromResult(project.Slug == slug);

    public Task<bool> TryAddProjectAsync(Project value, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<Project?> FindProjectAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Project?>(id == project.Id ? project : null);

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Project>>([project]);

    public void AddProvisioningOperations(IEnumerable<ProvisioningOperation> operations)
    {
    }

    public Task<ProvisioningOperation> AddProvisioningOperationAsync(
        ProvisioningOperation operation,
        CancellationToken cancellationToken = default) => Task.FromResult(operation);

    public Task CompleteProvisioningOperationAsync(
        Guid id,
        ProvisioningStatus status,
        string? error,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> NextProvisioningAttemptAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(1);

    public Task<IReadOnlyList<ProvisioningOperation>> ListProvisioningOperationsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProvisioningOperation>>([]);

    public Task DeleteProjectMetadataAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AddBackupAsync(ProjectBackup backup, CancellationToken cancellationToken = default)
    {
        Backups.Add(backup);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectBackup>> ListBackupsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectBackup>>(Backups);

    public Task<ProjectBackup?> FindBackupAsync(
        Guid projectId,
        Guid backupId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Backups.FirstOrDefault(backup => backup.Id == backupId));

    public Task RemoveBackupAsync(ProjectBackup backup, CancellationToken cancellationToken = default)
    {
        Backups.Remove(backup);
        return Task.CompletedTask;
    }

    public Task UpdateProjectStatusAsync(
        Guid projectId,
        ProjectStatus status,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        project.Status = status;
        project.FailureReason = failureReason;
        StatusUpdates.Add(status);
        return Task.CompletedTask;
    }

    public Task<bool> TryAddApiKeyAsync(ApiKey apiKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task<ApiKey?> FindApiKeyAsync(Guid projectId, Guid keyId, CancellationToken cancellationToken = default) =>
        Task.FromResult<ApiKey?>(null);

    public Task<IReadOnlyList<ApiKey>> ListApiKeysAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApiKey>>([]);

    public Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Audits.Add(entry);
        return Task.CompletedTask;
    }

    public Task<AuditPage> ListAuditEntriesAsync(
        AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<AuditEntry> entries = Audits;
        if (query.ProjectId is { } projectFilter)
        {
            entries = entries.Where(entry => entry.ProjectId == projectFilter);
        }
        if (query.Action is not null)
        {
            entries = entries.Where(entry => entry.Action == query.Action);
        }

        var matching = entries.ToArray();
        var items = matching
            .OrderByDescending(entry => entry.OccurredAt)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        return Task.FromResult(new AuditPage(items, matching.Length));
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakePgToolRunner(byte[] dumpBytes) : IPgToolRunner
{
    public int DumpExitCode { get; set; }

    public int RestoreExitCode { get; set; }

    public List<string> Invocations { get; } = [];

    public async Task<PgToolResult> RunAsync(PgToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        Invocations.Add(invocation.Program);
        if (invocation.Program.Contains("dump", StringComparison.Ordinal))
        {
            await invocation.StandardOutput.WriteAsync(dumpBytes, cancellationToken);
            return new PgToolResult(DumpExitCode, string.Empty);
        }

        return new PgToolResult(RestoreExitCode, string.Empty);
    }
}

internal sealed class FakeDatabaseCoordinator : IProjectDatabaseCoordinator
{
    public PgConnectionSettings ConnectionSettings { get; } =
        new("127.0.0.1", 5433, "admin", "unit-test-password");

    public bool LockAcquired { get; set; } = true;

    public bool TerminateCalled { get; private set; }

    public List<string> LockedDatabases { get; } = [];

    public Task<IProjectDatabaseLock> TryAcquireAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        LockedDatabases.Add(databaseName);
        return Task.FromResult<IProjectDatabaseLock>(
            LockAcquired ? new FakeDatabaseLock() : FakeDatabaseLock.Unacquired);
    }

    public Task TerminateOtherConnectionsAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        TerminateCalled = true;
        return Task.CompletedTask;
    }

    private sealed class FakeDatabaseLock : IProjectDatabaseLock
    {
        public static readonly FakeDatabaseLock Unacquired = new(false);

        private readonly bool _acquired;

        public FakeDatabaseLock() : this(true)
        {
        }

        private FakeDatabaseLock(bool acquired) => _acquired = acquired;

        public bool Acquired => _acquired;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
