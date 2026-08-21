using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("Bookings", t =>
        {
            t.HasCheckConstraint("CK_Bookings_Interval", "[EndsAtUtc] > [StartsAtUtc]");
            t.HasCheckConstraint("CK_Bookings_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_Bookings_Status",
                "[Status] IN ('Pending','Confirmed','Rejected','Cancelled','Completed','NoShow')");
        });
        builder.HasKey(b => b.Id).HasName("PK_Bookings");

        builder.Property(b => b.Title).HasMaxLength(200);
        builder.Property(b => b.CancellationReason).HasMaxLength(300);
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(b => b.StartsAtUtc).IsRequired();
        builder.Property(b => b.EndsAtUtc).IsRequired();
        builder.Property(b => b.Quantity).IsRequired();
        builder.Property(b => b.RowVersion).IsRowVersion();

        builder.Property(b => b.CreatedAtUtc).IsRequired();
        builder.Property(b => b.CreatedByUserId).IsRequired();
        builder.Property(b => b.UpdatedAtUtc).IsRequired();

        // Decision #6: composite FK against Resources' (OrgId, Id) alternate
        // key — a booking is physically unable to reference another tenant's
        // resource.
        builder.HasOne<Resource>().WithMany()
            .HasForeignKey(b => new { b.OrgId, b.ResourceId })
            .HasPrincipalKey(r => new { r.OrgId, r.Id })
            .HasConstraintName("FK_Bookings_Resources_SameOrg")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(b => b.UserId)
            .HasConstraintName("FK_Bookings_Users")
            .OnDelete(DeleteBehavior.NoAction);

        // Decision #2: TenantAdmin may cancel another user's booking, so this
        // is deliberately distinct from UpdatedByUserId.
        builder.HasOne<User>().WithMany()
            .HasForeignKey(b => b.CancelledByUserId)
            .HasConstraintName("FK_Bookings_CancelledBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<RecurrenceRule>().WithMany()
            .HasForeignKey(b => b.RecurrenceRuleId)
            .HasConstraintName("FK_Bookings_RecurrenceRules")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(b => b.CreatedByUserId)
            .HasConstraintName("FK_Bookings_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        // Nullable: the no-show release job flips Status with no human actor
        // (Decision #4).
        builder.HasOne<User>().WithMany()
            .HasForeignKey(b => b.UpdatedByUserId)
            .HasConstraintName("FK_Bookings_UpdatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        // Load-bearing (CLAUDE.md §4.1): keeps dbo.CreateBooking's
        // UPDLOCK/HOLDLOCK range lock narrow instead of table-wide. Never drop.
        builder.HasIndex(b => new { b.ResourceId, b.StartsAtUtc })
            .HasDatabaseName("IX_Bookings_Resource_Start")
            .IncludeProperties(b => new { b.EndsAtUtc, b.Status, b.Quantity });

        builder.HasIndex(b => new { b.OrgId, b.UserId, b.StartsAtUtc })
            .HasDatabaseName("IX_Bookings_User");

        builder.HasIndex(b => b.RecurrenceRuleId)
            .HasDatabaseName("IX_Bookings_Series")
            .HasFilter("[RecurrenceRuleId] IS NOT NULL");

        builder.HasIndex(b => b.StartsAtUtc)
            .HasDatabaseName("IX_Bookings_NoShowSweep")
            .HasFilter("[Status] = 'Confirmed' AND [CheckedInAtUtc] IS NULL");
    }
}
