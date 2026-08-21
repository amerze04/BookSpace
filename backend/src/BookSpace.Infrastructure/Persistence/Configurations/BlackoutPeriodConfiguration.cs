using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class BlackoutPeriodConfiguration : IEntityTypeConfiguration<BlackoutPeriod>
{
    public void Configure(EntityTypeBuilder<BlackoutPeriod> builder)
    {
        builder.ToTable("BlackoutPeriods", t => t.HasCheckConstraint(
            "CK_BlackoutPeriods_Interval", "[EndsAtUtc] > [StartsAtUtc]"));
        builder.HasKey(b => b.Id).HasName("PK_BlackoutPeriods");

        builder.Property(b => b.Reason).HasMaxLength(300);
        builder.Property(b => b.StartsAtUtc).IsRequired();
        builder.Property(b => b.EndsAtUtc).IsRequired();

        builder.Property(b => b.CreatedAtUtc).IsRequired();
        builder.Property(b => b.CreatedByUserId).IsRequired();
        builder.Property(b => b.UpdatedAtUtc).IsRequired();

        builder.HasOne<Resource>().WithMany()
            .HasForeignKey(b => b.ResourceId)
            .HasConstraintName("FK_BlackoutPeriods_Resources")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(b => b.CreatedByUserId)
            .HasConstraintName("FK_BlackoutPeriods_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(b => b.UpdatedByUserId)
            .HasConstraintName("FK_BlackoutPeriods_UpdatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(b => new { b.ResourceId, b.StartsAtUtc })
            .HasDatabaseName("IX_BlackoutPeriods_Resource_Start");
    }
}
