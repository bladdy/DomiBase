using DomiBase.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");

        builder.Property(p => p.Slug)
            .HasColumnName("slug")
            .HasMaxLength(ProjectNaming.MaxSlugLength)
            .IsRequired();
        builder.HasIndex(p => p.Slug).IsUnique();

        builder.Property(p => p.Name)
            .HasColumnName("name")
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(p => p.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(p => p.DatabaseName)
            .HasColumnName("database_name")
            .HasMaxLength(ProjectNaming.MaxDatabaseNameLength)
            .IsRequired();
        builder.HasIndex(p => p.DatabaseName).IsUnique();

        builder.Property(p => p.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(1000);

        builder.Property(p => p.RuntimePasswordCiphertext)
            .HasColumnName("runtime_password_ciphertext");

        builder.Property(p => p.RateLimitPermits)
            .HasColumnName("rate_limit_permits");
        builder.Property(p => p.RateLimitWindowSeconds)
            .HasColumnName("rate_limit_window_seconds");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");

        builder.HasMany(p => p.ApiKeys)
            .WithOne(k => k.Project)
            .HasForeignKey(k => k.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.ProvisioningOperations)
            .WithOne(o => o.Project)
            .HasForeignKey(o => o.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
