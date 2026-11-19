using System.Security.Cryptography;
using System.Text;
using DomiBase.Application.Backups;
using DomiBase.Application.Projects;
using DomiBase.Application.Schema;
using DomiBase.Domain.Projects;
using DomiBase.Infrastructure.Backups;
using Microsoft.Extensions.Logging.Abstractions;

namespace DomiBase.UnitTests;

/// <summary>
/// Orchestration-level tests. A real filesystem store is used with fake PostgreSQL tools, so
/// integrity checks, audit records and status transitions are exercised without Docker.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private const string Slug = "backup-unit";
    private static readonly byte[] DumpBytes = Encoding.UTF8.GetBytes("PGDMP-fake-dump-content");

    private readonly string _root;
    private readonly Project _project;

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "domibase-backupservice-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _project = new Project
        {
            Slug = Slug,
            Name = "Backup unit tests",
            DatabaseName = ProjectNaming.ToDatabaseName(Slug),
            Status = ProjectStatus.Ready
        };
    }

    [Fact]
    public async Task Create_WritesDumpStoresHashAndAuditsTheOperation()
    {
        var (service, repository, coordinator, _, _) = CreateService();

        var result = await service.CreateAsync(_project.Id);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Single(repository.Backups);
        Assert.Equal("custom", result.Value!.Format);
        Assert.Equal(DumpBytes.Length, result.Value.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(DumpBytes)).ToLowerInvariant(),
            result.Value.Sha256);
        Assert.True(File.Exists(Path.Combine(_root, repository.Backups[0].FileName)));
        Assert.Contains(repository.Audits, audit => audit.Action == "project.backup.created");
        Assert.Equal([ProjectNaming.ToDatabaseName(Slug)], coordinator.LockedDatabases);
    }

    [Fact]
    public async Task Create_WhenToolFails_RecordsNoMetadataAndRemovesTheTemporaryFile()
    {
        var (service, repository, _, runner, _) = CreateService();
        runner.DumpExitCode = 1;

        var result = await service.CreateAsync(_project.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.Conflict, result.Error!.Code);
        Assert.Empty(repository.Backups);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.Contains(repository.Audits, audit => audit.Action == "project.backup.failed");
    }

    [Fact]
    public async Task Create_WhenLocked_ReturnsConflictWithoutRunningTheTool()
    {
        var (service, repository, coordinator, runner, _) = CreateService();
        coordinator.LockAcquired = false;

        var result = await service.CreateAsync(_project.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.Conflict, result.Error!.Code);
        Assert.Empty(runner.Invocations);
        Assert.Empty(repository.Backups);
    }

    [Fact]
    public async Task Restore_WithWrongConfirmation_IsRejectedBeforeAnyDatabaseWork()
    {
        var (service, repository, coordinator, runner, _) = CreateService();
        var backupId = await CreateBackupAsync(service);

        var result = await service.RestoreAsync(_project.Id, backupId, "wrong-slug");

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.Validation, result.Error!.Code);
        Assert.Equal([ProjectNaming.ToDatabaseName(Slug)], coordinator.LockedDatabases);
        Assert.False(coordinator.TerminateCalled);
        Assert.DoesNotContain("pg_restore", runner.Invocations);
        Assert.Equal(ProjectStatus.Ready, _project.Status);
    }

    [Fact]
    public async Task Restore_WithCorruptedDump_IsRejectedBeforeAnyDatabaseWork()
    {
        var (service, repository, coordinator, runner, _) = CreateService();
        var backupId = await CreateBackupAsync(service);
        var file = Path.Combine(_root, repository.Backups[0].FileName);
        await File.WriteAllBytesAsync(file, Encoding.UTF8.GetBytes("tampered-dump"));

        var result = await service.RestoreAsync(_project.Id, backupId, Slug);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.Validation, result.Error!.Code);
        Assert.False(coordinator.TerminateCalled);
        Assert.DoesNotContain("pg_restore", runner.Invocations);
        Assert.Equal(ProjectStatus.Ready, _project.Status);
    }

    [Fact]
    public async Task Restore_WhenToolFails_MarksProjectFailedAndAuditsTheFailure()
    {
        var (service, repository, coordinator, runner, _) = CreateService();
        var backupId = await CreateBackupAsync(service);
        runner.RestoreExitCode = 1;

        var result = await service.RestoreAsync(_project.Id, backupId, Slug);

        Assert.False(result.Succeeded);
        Assert.Equal(ControlErrorCode.Conflict, result.Error!.Code);
        Assert.Equal(ProjectStatus.Failed, _project.Status);
        Assert.True(coordinator.TerminateCalled);
        Assert.Contains(repository.Audits, audit => audit.Action == "project.backup.restore_started");
        Assert.Contains(repository.Audits, audit => audit.Action == "project.backup.restore_failed");
    }

    [Fact]
    public async Task Restore_SucceedsAndReturnsProjectToReady()
    {
        var (service, repository, coordinator, runner, _) = CreateService();
        var backupId = await CreateBackupAsync(service);

        var result = await service.RestoreAsync(_project.Id, backupId, Slug);

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(ProjectStatus.Ready, _project.Status);
        Assert.Equal([ProjectStatus.Provisioning, ProjectStatus.Ready], repository.StatusUpdates);
        Assert.True(coordinator.TerminateCalled);
        Assert.Contains("pg_restore", runner.Invocations);
        Assert.Contains(repository.Audits, audit => audit.Action == "project.backup.restored");
    }

    [Fact]
    public async Task Delete_RemovesFileAndMetadata()
    {
        var (service, repository, _, _, _) = CreateService();
        var backupId = await CreateBackupAsync(service);
        var file = Path.Combine(_root, repository.Backups[0].FileName);

        var result = await service.DeleteAsync(_project.Id, backupId);

        Assert.True(result.Succeeded);
        Assert.Empty(repository.Backups);
        Assert.False(File.Exists(file));
        Assert.Contains(repository.Audits, audit => audit.Action == "project.backup.deleted");
    }

    private async Task<Guid> CreateBackupAsync(BackupService service)
    {
        var result = await service.CreateAsync(_project.Id);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Value!.Id;
    }

    private (
        BackupService Service,
        FakeProjectRepository Repository,
        FakeDatabaseCoordinator Coordinator,
        FakePgToolRunner Runner,
        BackupOptions Options) CreateService()
    {
        var repository = new FakeProjectRepository(_project);
        var coordinator = new FakeDatabaseCoordinator();
        var runner = new FakePgToolRunner(DumpBytes);
        var options = new BackupOptions
        {
            RootPath = _root,
            ToolMode = BackupToolMode.Docker,
            DockerContainer = "test-postgres"
        };
        var store = new FileSystemBackupStore(options);
        var service = new BackupService(
            repository,
            new ProjectSchemaResolver(repository),
            store,
            runner,
            coordinator,
            options,
            NullLogger<BackupService>.Instance);

        return (service, repository, coordinator, runner, options);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
