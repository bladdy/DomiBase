using DomiBase.Domain.Functions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DomiBase.Infrastructure.Persistence.Configurations;

public sealed class FunctionDefinitionConfiguration : IEntityTypeConfiguration<FunctionDefinition>
{
    public void Configure(EntityTypeBuilder<FunctionDefinition> builder)
    {
        builder.ToTable("functions");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Name)
            .HasMaxLength(63)
            .IsRequired();

        builder.Property(f => f.Description)
            .HasMaxLength(256);

        builder.Property(f => f.Module)
            .IsRequired();

        builder.Property(f => f.ModuleSha256)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();

        builder.HasIndex(f => new { f.ProjectId, f.Name })
            .IsUnique();
    }
}
