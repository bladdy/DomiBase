using DomiBase.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");

        builder.Property(a => a.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();
        builder.HasIndex(a => a.OccurredAt);

        builder.Property(a => a.Actor)
            .HasColumnName("actor")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(a => a.Action)
            .HasColumnName("action")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.TargetType)
            .HasColumnName("target_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.TargetId)
            .HasColumnName("target_id")
            .HasMaxLength(64);

        builder.Property(a => a.ProjectId).HasColumnName("project_id");
        builder.HasIndex(a => a.ProjectId);

        builder.Property(a => a.Details)
            .HasColumnName("details")
            .HasColumnType("jsonb");

        builder.Property(a => a.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(64);
    }
}
