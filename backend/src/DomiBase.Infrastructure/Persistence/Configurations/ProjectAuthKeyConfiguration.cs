using DomiBase.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class ProjectAuthKeyConfiguration : IEntityTypeConfiguration<ProjectAuthKey>
{
    public void Configure(EntityTypeBuilder<ProjectAuthKey> builder)
    {
        builder.ToTable("project_auth_keys");

        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).HasColumnName("id");

        builder.Property(k => k.ProjectId).HasColumnName("project_id");

        builder.Property(k => k.KeyId)
            .HasColumnName("key_id")
            .HasMaxLength(32)
            .IsRequired();
        builder.HasIndex(k => k.KeyId).IsUnique();

        builder.Property(k => k.PublicKey)
            .HasColumnName("public_key")
            .IsRequired();

        builder.Property(k => k.PrivateKeyCiphertext)
            .HasColumnName("private_key_ciphertext")
            .IsRequired();

        builder.Property(k => k.CreatedAt).HasColumnName("created_at");
        builder.Property(k => k.RetiredAt).HasColumnName("retired_at");

        // At most one active signing key per project; concurrent creation converges on this index.
        builder.HasIndex(k => k.ProjectId)
            .HasFilter("retired_at IS NULL")
            .IsUnique();

        builder.HasOne(k => k.Project)
            .WithMany(p => p.AuthSigningKeys)
            .HasForeignKey(k => k.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
