using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations", t => t.HasCheckConstraint(
            "CK_Organizations_Status", "[Status] IN ('Active','Suspended')"));
        builder.HasKey(o => o.Id).HasName("PK_Organizations");
        builder.HasAlternateKey(o => o.Slug).HasName("UQ_Organizations_Slug");

        builder.Property(o => o.Name).HasMaxLength(200).IsRequired();
        builder.Property(o => o.Slug).HasMaxLength(60).IsRequired();
        builder.Property(o => o.TimeZoneId).HasMaxLength(60).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.ReminderLeadMinutes).IsRequired();
        builder.Property(o => o.NoShowGraceMinutes).IsRequired();

        builder.Property(o => o.CreatedAtUtc).IsRequired();
        builder.Property(o => o.CreatedByUserId).IsRequired();
        builder.Property(o => o.UpdatedAtUtc).IsRequired();

        builder.HasOne<User>().WithMany()
            .HasForeignKey(o => o.CreatedByUserId)
            .HasConstraintName("FK_Organizations_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(o => o.UpdatedByUserId)
            .HasConstraintName("FK_Organizations_UpdatedBy")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
