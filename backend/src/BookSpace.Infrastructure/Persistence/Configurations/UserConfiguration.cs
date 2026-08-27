using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookSpace.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(u => u.Id).HasName("PK_Users");
        builder.HasAlternateKey(u => u.CalendarFeedToken).HasName("UQ_Users_CalendarFeedToken");

        builder.Property(u => u.Email).HasMaxLength(320).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(255).IsRequired();
        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();
        builder.Property(u => u.IsActive).IsRequired();
        builder.Property(u => u.CalendarFeedToken).IsRequired();

        builder.Property(u => u.CreatedAtUtc).IsRequired();
        builder.Property(u => u.CreatedByUserId).IsRequired();
        builder.Property(u => u.UpdatedAtUtc).IsRequired();

        // Owned collection over the private _roleAssignments backing field —
        // SaveChanges adds/removes UserRoles rows on its own as Roles changes,
        // no repository-level diff needed (see User.RoleAssignment).
        builder.OwnsMany<User.RoleAssignment>("_roleAssignments", nav =>
        {
            nav.ToTable("UserRoles", t => t.HasCheckConstraint(
                "CK_UserRoles_Role", "[Role] IN ('SysAdmin','TenantAdmin','Approver','Member')"));
            nav.WithOwner().HasForeignKey("UserId").HasConstraintName("FK_UserRoles_Users");
            nav.Property(r => r.Role).HasConversion<string>().HasMaxLength(20);
            nav.HasKey("UserId", "Role").HasName("PK_UserRoles");
        });
        builder.Navigation("_roleAssignments").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne<Organization>().WithMany()
            .HasForeignKey(u => u.OrgId)
            .HasConstraintName("FK_Users_Organizations")
            .OnDelete(DeleteBehavior.NoAction);

        // Self-referencing: CLAUDE.md — no self-registration, every user is
        // provisioned by a SysAdmin/TenantAdmin; the bootstrap SysAdmin
        // self-references its own Id.
        builder.HasOne<User>().WithMany()
            .HasForeignKey(u => u.CreatedByUserId)
            .HasConstraintName("FK_Users_CreatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(u => u.UpdatedByUserId)
            .HasConstraintName("FK_Users_UpdatedBy")
            .OnDelete(DeleteBehavior.NoAction);

        // Globally unique, unfiltered: an email identifies exactly one user across
        // the whole platform, so login needs no tenant discriminator. Replaces the
        // old (OrgId, Email) filtered index, which both allowed the same email in
        // two tenants and left SysAdmin rows (OrgId NULL) with no uniqueness at
        // all (docs/decisions/0010-global-email-uniqueness.md).
        builder.HasIndex(u => u.Email)
            .IsUnique()
            .HasDatabaseName("UQ_Users_Email");
    }
}
