using DomiBase.Domain.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

internal sealed class ProjectBackupConfiguration : IEntityTypeConfiguration<ProjectBackup>
{
    public void Configure(EntityTypeBuilder<ProjectBackup> builder)
    {
        builder.ToTable("project_backups");
        builder.HasKey(backup => backup.Id);
        builder.Property(backup => backup.ProjectId).IsRequired();
        builder.Property(backup => backup.FileName).HasMaxLength(256).IsRequired();
        builder.Property(backup => backup.Format).HasMaxLength(32).IsRequired();
        builder.Property(backup => backup.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(backup => backup.CreatedAt).IsRequired();

        builder.HasIndex(backup => backup.ProjectId);
    }
}
