using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ToTable("Resources", t => t.HasCheckConstraint(
            "CK_Resources_Capacity", "[Capacity] > 0"));
        builder.HasKey(r => r.Id).HasName("PK_Resources");
        // Alternate key: target of Bookings' composite tenant FK (Decision #6).
        builder.HasAlternateKey(r => new { r.OrgId, r.Id }).HasName("UQ_Resources_Org_Id");

        builder.Property(r => r.Name).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(1000);
        builder.Property(r => r.ResourceType).HasMaxLength(50).IsRequired();
        builder.Property(r => r.Capacity).IsRequired();
        builder.Property(r => r.TimeZoneId).HasMaxLength(60).IsRequired();
        builder.Property(r => r.RequiresApproval).IsRequired();
        builder.Property(r => r.IsArchived).IsRequired();

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.CreatedByUserId).IsRequired();
        builder.Property(r => r.UpdatedAtUtc).IsRequired();

        // Owned collection over the private _approverAssignments backing
        // field — same technique as User.Roles in UserConfiguration.cs.
        builder.OwnsMany<Resource.ApproverAssignment>("_approverAssignments", nav =>
        {
            nav.ToTable("ResourceApprovers");
            // Known, accepted deviation from CLAUDE.md §5 (only UserRoles,
            // RefreshTokens, AvailabilityWindows, BlackoutPeriods and
            // Notifications are whitelisted for cascade): EF forces owned-
            // collection relationships to cascade delete — OwnershipBuilder
            // exposes no .OnDelete() override — whereas the schema's
            // FK_ResourceApprovers_Resources is NoAction. Left as-is because
            // it's inert in practice: Resources are archived, never deleted
            // (CLAUDE.md §4.5). Revisit only if that assumption changes, in
            // which case the fix is to stop modeling this as an owned type.
            nav.WithOwner().HasForeignKey("ResourceId")
                .HasConstraintName("FK_ResourceApprovers_Resources");
            nav.HasKey("ResourceId", "UserId").HasName("PK_ResourceApprovers");
            nav.HasOne<User>().WithMany()
                .HasForeignKey(a => a.UserId)
                .HasConstraintName("FK_ResourceApprovers_Users")
                .OnDelete(DeleteBehavior.NoAction);
        });
        builder.Navigation("_approverAssignments").UsePropertyAccessMode(PropertyAccessMode.Field);

        // AvailabilityWindows IS a real entity (its own Id/table), so it's a
        // normal collection navigation — just needs field access since the
        // property only exposes an AsReadOnly() wrapper, never the list itself.
        builder.HasMany(r => r.AvailabilityWindows).WithOne()
            .HasForeignKey(w => w.ResourceId)
            .HasConstraintName("FK_AvailabilityWindows_Resources")
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.AvailabilityWindows)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne<Organization>().WithMany()
            .HasForeignKey(r => r.OrgId)
            .HasConstraintName("FK_Resources_Organizations")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => r.CreatedByUserId)
            .HasConstraintName("FK_Resources_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => r.UpdatedByUserId)
            .HasConstraintName("FK_Resources_UpdatedBy")
            .OnDelete(DeleteBehavior.NoAction);
    }
}
