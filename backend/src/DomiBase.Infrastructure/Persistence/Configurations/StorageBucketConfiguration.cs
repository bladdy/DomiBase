using DomiBase.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class StorageBucketConfiguration : IEntityTypeConfiguration<StorageBucket>
{
    public void Configure(EntityTypeBuilder<StorageBucket> builder)
    {
        builder.ToTable("storage_buckets");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Name)
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(b => b.PublicAccess)
            .IsRequired();

        builder.Property(b => b.CreatedAt)
            .IsRequired();

        builder.Property(b => b.UpdatedAt)
            .IsRequired();

        builder.HasIndex(b => new { b.ProjectId, b.Name })
            .IsUnique();

        builder.HasMany(b => b.Objects)
            .WithOne(o => o.Bucket)
            .HasForeignKey(o => o.BucketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
