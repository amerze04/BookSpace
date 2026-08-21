using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> builder)
    {
        builder.ToTable("ApprovalRequests", t =>
        {
            t.HasCheckConstraint("CK_ApprovalRequests_Decision",
                "[Decision] IN ('Pending','Approved','Rejected','Expired')");
            t.HasCheckConstraint("CK_ApprovalRequests_DecisionPaired",
                "([Decision] = 'Pending' AND [DecidedByUserId] IS NULL AND [DecidedAtUtc] IS NULL) " +
                "OR ([Decision] <> 'Pending' AND [DecidedAtUtc] IS NOT NULL)");
        });
        builder.HasKey(a => a.Id).HasName("PK_ApprovalRequests");
        builder.HasAlternateKey(a => a.BookingId).HasName("UQ_ApprovalRequests_Booking");

        builder.Property(a => a.Note).HasMaxLength(500);
        builder.Property(a => a.Decision).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.RequestedAtUtc).IsRequired();

        builder.HasOne<Booking>().WithOne()
            .HasForeignKey<ApprovalRequest>(a => a.BookingId)
            .HasConstraintName("FK_ApprovalRequests_Bookings")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(a => a.DecidedByUserId)
            .HasConstraintName("FK_ApprovalRequests_Users")
            .OnDelete(DeleteBehavior.NoAction);

        // FR-9.3 stale-approval sweep.
        builder.HasIndex(a => a.ExpiresAtUtc)
            .HasDatabaseName("IX_ApprovalRequests_Pending")
            .HasFilter("[Decision] = 'Pending'");
    }
}
