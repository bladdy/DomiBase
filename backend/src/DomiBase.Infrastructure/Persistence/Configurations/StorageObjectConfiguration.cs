using DomiBase.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class StorageObjectConfiguration : IEntityTypeConfiguration<StorageObject>
{
    public void Configure(EntityTypeBuilder<StorageObject> builder)
    {
        builder.ToTable("storage_objects");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Path)
            .HasMaxLength(1024)
            .IsRequired();

        builder.Property(o => o.ContentType)
            .HasMaxLength(120);

        builder.Property(o => o.Size)
            .IsRequired();

        builder.Property(o => o.Etag)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(o => o.CreatedAt)
            .IsRequired();

        builder.Property(o => o.UpdatedAt)
            .IsRequired();

        builder.HasIndex(o => new { o.BucketId, o.Path })
            .IsUnique();
    }
}
