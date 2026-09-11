using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, P2/3 security — cross-tenant user FK. Bookings.UserId,
    // CreatedByUserId, UpdatedByUserId and CancelledByUserId were plain FKs to
    // Users(Id), unlike ResourceId, which already has a composite same-org FK
    // against Resources' (OrgId, Id) alternate key (decision 0006). This adds
    // the matching guarantee for all four user columns: a booking can no
    // longer reference another tenant's user, full stop, rather than that
    // being true only because every current write path happens to derive
    // these ids from an authenticated, same-tenant actor (ICurrentUser) or a
    // same-tenant lookup (BookingReadRules) — a real boundary this makes
    // structural rather than merely observed.
    //
    // **Raw SQL, not EF's HasAlternateKey/HasForeignKey(...).HasPrincipalKey(...)
    // model API**, and that is not a style choice: EF Core requires every
    // column in an alternate key to be non-nullable, and Users.OrgId is
    // nullable by design — a SysAdmin genuinely has no organization (decision
    // 0009). Asking EF to declare `HasAlternateKey(u => new { u.OrgId, u.Id })`
    // makes it scaffold an `ALTER COLUMN OrgId ... NOT NULL DEFAULT
    // '00000000-...'` migration, which would have silently reassigned every
    // SysAdmin row to a fake tenant — caught before this migration was
    // written, not after. SQL Server itself has no such restriction: a UNIQUE
    // constraint over a nullable column is ordinary, and a composite FK whose
    // own column is NULL is simply exempt from the check (the same NULL
    // semantics the existing single-column nullable FKs — UpdatedByUserId,
    // CancelledByUserId — already rely on). This is the same category
    // CLAUDE.md §5 already puts stored procedures and RLS policies in: added
    // as SQL because EF's C# model cannot express them, not because they are
    // any less real a part of the schema.
    //
    // **Additive, not a replacement.** The four existing single-column FKs
    // (FK_Bookings_Users, _CreatedBy, _UpdatedBy, _CancelledBy) are left in
    // place untouched — they still guarantee "this id is a real user in
    // *some* tenant" independent of OrgId, and adding the composite
    // constraints alongside them (rather than dropping and recombining) is
    // the smaller, more reviewable change: two independent constraints that
    // must both hold, instead of one constraint doing two jobs.
    public partial class AddCrossTenantUserForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE dbo.Users
                    ADD CONSTRAINT UQ_Users_Org_Id UNIQUE (OrgId, Id);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE dbo.Bookings
                    ADD CONSTRAINT FK_Bookings_Users_SameOrg
                    FOREIGN KEY (OrgId, UserId) REFERENCES dbo.Users (OrgId, Id);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE dbo.Bookings
                    ADD CONSTRAINT FK_Bookings_CreatedBy_SameOrg
                    FOREIGN KEY (OrgId, CreatedByUserId) REFERENCES dbo.Users (OrgId, Id);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE dbo.Bookings
                    ADD CONSTRAINT FK_Bookings_UpdatedBy_SameOrg
                    FOREIGN KEY (OrgId, UpdatedByUserId) REFERENCES dbo.Users (OrgId, Id);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE dbo.Bookings
                    ADD CONSTRAINT FK_Bookings_CancelledBy_SameOrg
                    FOREIGN KEY (OrgId, CancelledByUserId) REFERENCES dbo.Users (OrgId, Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE dbo.Bookings DROP CONSTRAINT FK_Bookings_CancelledBy_SameOrg;");
            migrationBuilder.Sql("ALTER TABLE dbo.Bookings DROP CONSTRAINT FK_Bookings_UpdatedBy_SameOrg;");
            migrationBuilder.Sql("ALTER TABLE dbo.Bookings DROP CONSTRAINT FK_Bookings_CreatedBy_SameOrg;");
            migrationBuilder.Sql("ALTER TABLE dbo.Bookings DROP CONSTRAINT FK_Bookings_Users_SameOrg;");
            migrationBuilder.Sql("ALTER TABLE dbo.Users DROP CONSTRAINT UQ_Users_Org_Id;");
        }
    }
}
