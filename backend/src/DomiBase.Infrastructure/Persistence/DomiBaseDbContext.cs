using DomiBase.Domain.Auditing;
using DomiBase.Domain.Backups;
using DomiBase.Domain.Functions;
using DomiBase.Domain.Projects;
using DomiBase.Domain.Storage;
using Microsoft.EntityFrameworkCore;

namespace DomiBase.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the Control Plane metadata database (<c>domibase_control</c>).
/// Project data lives in independent databases and is never mapped here.
/// </summary>
public sealed class DomiBaseDbContext(DbContextOptions<DomiBaseDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<ProvisioningOperation> ProvisioningOperations => Set<ProvisioningOperation>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<ProjectBackup> ProjectBackups => Set<ProjectBackup>();

    public DbSet<ProjectAuthKey> ProjectAuthKeys => Set<ProjectAuthKey>();

    public DbSet<StorageBucket> StorageBuckets => Set<StorageBucket>();

    public DbSet<StorageObject> StorageObjects => Set<StorageObject>();

    public DbSet<FunctionDefinition> Functions => Set<FunctionDefinition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DomiBaseDbContext).Assembly);
    }
}
