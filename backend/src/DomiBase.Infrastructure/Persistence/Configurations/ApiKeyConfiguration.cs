using DomiBase.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> builder)
    {
        builder.ToTable("api_keys");

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).HasColumnName("id");

        builder.Property(k => k.ProjectId)
            .HasColumnName("project_id")
            .IsRequired();
        builder.HasIndex(k => k.ProjectId);

        builder.Property(k => k.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(k => k.Prefix)
            .HasColumnName("prefix")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(k => k.SecretHash)
            .HasColumnName("secret_hash")
            .HasMaxLength(128)
            .IsRequired();
        builder.HasIndex(k => k.SecretHash).IsUnique();

        builder.Property(k => k.Label)
            .HasColumnName("label")
            .HasMaxLength(120);

        builder.Property(k => k.CreatedAt).HasColumnName("created_at");
        builder.Property(k => k.RevokedAt).HasColumnName("revoked_at");
        builder.Property(k => k.LastUsedAt).HasColumnName("last_used_at");
        builder.Property(k => k.ExpiresAt).HasColumnName("expires_at");

        builder.Property(k => k.PreviousSecretHash)
            .HasColumnName("previous_secret_hash")
            .HasMaxLength(128);
        builder.HasIndex(k => k.PreviousSecretHash);
        builder.Property(k => k.PreviousHashExpiresAt).HasColumnName("previous_hash_expires_at");

        builder.Ignore(k => k.IsRevoked);
    }
}
