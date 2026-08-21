using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class RecurrenceRuleConfiguration : IEntityTypeConfiguration<RecurrenceRule>
{
    public void Configure(EntityTypeBuilder<RecurrenceRule> builder)
    {
        builder.ToTable("RecurrenceRules", t =>
        {
            t.HasCheckConstraint("CK_RecurrenceRules_Frequency", "[Frequency] IN ('Daily','Weekly','Monthly')");
            t.HasCheckConstraint("CK_RecurrenceRules_Interval", "[IntervalValue] > 0");
            t.HasCheckConstraint("CK_RecurrenceRules_Status", "[Status] IN ('Active','Cancelled')");
            t.HasCheckConstraint("CK_RecurrenceRules_EndCondition",
                "([EndDate] IS NOT NULL AND [OccurrenceCount] IS NULL) OR ([EndDate] IS NULL AND [OccurrenceCount] IS NOT NULL)");
            // Decision #7: only the EndDate case is checkable in SQL; the
            // OccurrenceCount case is enforced in the RecurrenceRule
            // constructor instead (see Domain).
            t.HasCheckConstraint("CK_RecurrenceRules_MaxSpan",
                "[EndDate] IS NULL OR [EndDate] <= DATEADD(YEAR, 2, [StartDate])");
        });
        builder.HasKey(r => r.Id).HasName("PK_RecurrenceRules");

        builder.Property(r => r.Frequency).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.TimeZoneId).HasMaxLength(60).IsRequired();
        builder.Property(r => r.IntervalValue).IsRequired();
        builder.Property(r => r.LocalStartTime).HasPrecision(0).IsRequired();
        builder.Property(r => r.LocalEndTime).HasPrecision(0).IsRequired();
        builder.Property(r => r.StartDate).IsRequired();

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.CreatedByUserId).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        builder.HasOne<Resource>().WithMany()
            .HasForeignKey(r => r.ResourceId)
            .HasConstraintName("FK_RecurrenceRules_Resources")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => r.UserId)
            .HasConstraintName("FK_RecurrenceRules_Users")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => r.CreatedByUserId)
            .HasConstraintName("FK_RecurrenceRules_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => r.UpdatedByUserId)
            .HasConstraintName("FK_RecurrenceRules_UpdatedBy")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
