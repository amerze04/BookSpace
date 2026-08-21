using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications", t =>
        {
            t.HasCheckConstraint("CK_Notifications_HasContext",
                "[BookingId] IS NOT NULL OR ([RecurrenceRuleId] IS NOT NULL AND [OccurrenceDate] IS NOT NULL)");
            t.HasCheckConstraint("CK_Notifications_Kind",
                "[Kind] IN ('Confirmed','Rejected','Cancelled','Reminder','ApprovalRequested','NoShowReleased','RecurrenceOccurrenceSkipped')");
        });
        builder.HasKey(n => n.Id).HasName("PK_Notifications");

        builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(n => n.LastError).HasMaxLength(500);
        builder.Property(n => n.SendAtUtc).IsRequired();
        builder.Property(n => n.Attempts).IsRequired();

        builder.Property(n => n.CreatedAtUtc).IsRequired();
        builder.Property(n => n.UpdatedAtUtc).IsRequired();

        // UQ_Notifications_Once involves two nullable columns (RecurrenceRuleId,
        // OccurrenceDate — Decision #8's dual anchor), which EF's HasAlternateKey
        // rejects; a unique index enforces the identical constraint in SQL
        // Server without that restriction. HasFilter(null) is required: EF's
        // default convention would otherwise silently filter out any row with a
        // null in a nullable indexed column — which is every Booking-anchored
        // notification (RecurrenceRuleId/OccurrenceDate are always null there),
        // defeating AC-6 idempotence for the common case. The schema's
        // constraint is unfiltered — SQL Server treats an all-matching key,
        // nulls included, as a duplicate for a composite unique constraint.
        builder.HasIndex(n => new { n.BookingId, n.RecurrenceRuleId, n.OccurrenceDate, n.RecipientUserId, n.Kind })
            .IsUnique()
            .HasDatabaseName("UQ_Notifications_Once")
            .HasFilter(null);

        builder.HasOne<Booking>().WithMany()
            .HasForeignKey(n => n.BookingId)
            .HasConstraintName("FK_Notifications_Bookings")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RecurrenceRule>().WithMany()
            .HasForeignKey(n => n.RecurrenceRuleId)
            .HasConstraintName("FK_Notifications_RecurrenceRules")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(n => n.RecipientUserId)
            .HasConstraintName("FK_Notifications_Users")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(n => n.CreatedByUserId)
            .HasConstraintName("FK_Notifications_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        // FR-9.2 worker poll.
        builder.HasIndex(n => n.SendAtUtc)
            .HasDatabaseName("IX_Notifications_Due")
            .HasFilter("[SentAtUtc] IS NULL");
    }
}
