using DomiBase.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class ProvisioningOperationConfiguration : IEntityTypeConfiguration<ProvisioningOperation>
{
    public void Configure(EntityTypeBuilder<ProvisioningOperation> builder)
    {
        builder.ToTable("provisioning_operations");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id");

        builder.Property(o => o.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();
        builder.HasIndex(o => o.ProjectId);

        builder.Property(o => o.Step)
            .HasColumnName("step")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(o => o.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(o => o.Attempt).HasColumnName("attempt");

        builder.Property(o => o.Error)
            .HasColumnName("error")
            .HasMaxLength(2000);

        builder.Property(o => o.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(64);

        builder.Property(o => o.StartedAt).HasColumnName("started_at");
        builder.Property(o => o.CompletedAt).HasColumnName("completed_at");
    }
}
