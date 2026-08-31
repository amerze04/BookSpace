using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // WP-3 decision D1 (docs/wp3-plan.md): AvailabilityWindows and
    // BlackoutPeriods get their own OrgId so they sit inside all three
    // CLAUDE.md §4.2 isolation mechanisms instead of being reachable by
    // ResourceId alone. Follows the precedent in
    // docs/decisions/0006-orgid-denormalization.md — denormalize the column,
    // then make the two values physically unable to disagree via a composite
    // FK against Resources' (OrgId, Id) alternate key (UQ_Resources_Org_Id).
    //
    // Hand-edited after scaffolding, in three ways:
    //
    //  1. The scaffold added OrgId as NOT NULL DEFAULT '00000000-0000-...',
    //     which would stamp every existing row with an OrgId no Organization
    //     owns and then fail the composite FK. Instead: add it nullable,
    //     backfill from the owning Resource, then ALTER to NOT NULL.
    //
    //  2. The backfill reads dbo.Resources, which Security.TenantAccessPolicy
    //     already filters, and this connection cannot grant itself a bypass:
    //     TenantSessionContextInterceptor sets the session context with
    //     @read_only = 1, so TenantBypass cannot be re-set for a session it has
    //     already initialized. With no tenant context the join would match zero
    //     rows and the backfill would be a silent no-op — leaving NULLs that the
    //     following ALTER COLUMN then fails on. So the policy is switched off for
    //     the duration and back on at the end. That is a global switch for the
    //     length of the migration, which is acceptable for a schema change and is
    //     why this is the only place that does it.
    //
    //  3. The two new filter predicates (mechanism 3) are added last. A filter
    //     predicate schema-binds the policy to the column, and SQL Server will
    //     not let ALTER COLUMN touch a column a security policy references.
    public partial class AddChildTableTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER SECURITY POLICY Security.TenantAccessPolicy WITH (STATE = OFF);");

            migrationBuilder.DropForeignKey(
                name: "FK_AvailabilityWindows_Resources",
                table: "AvailabilityWindows");

            migrationBuilder.DropForeignKey(
                name: "FK_BlackoutPeriods_Resources",
                table: "BlackoutPeriods");

            // EF's own FK index, superseded by IX_AvailabilityWindows_OrgId_ResourceId
            // below. BlackoutPeriods has no equivalent — its FK was already served
            // by the hand-named IX_BlackoutPeriods_Resource_Start.
            migrationBuilder.DropIndex(
                name: "IX_AvailabilityWindows_ResourceId",
                table: "AvailabilityWindows");

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "AvailabilityWindows",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "BlackoutPeriods",
                type: "uniqueidentifier",
                nullable: true);

            // Backfill: the owning resource is the only authority on which tenant
            // one of these rows belongs to. Every row has one (ResourceId was
            // already NOT NULL), so no row is left NULL for the ALTER below.
            migrationBuilder.Sql(
                """
                UPDATE w
                SET w.OrgId = r.OrgId
                FROM dbo.AvailabilityWindows AS w
                INNER JOIN dbo.Resources AS r ON r.Id = w.ResourceId;
                """);

            migrationBuilder.Sql(
                """
                UPDATE b
                SET b.OrgId = r.OrgId
                FROM dbo.BlackoutPeriods AS b
                INNER JOIN dbo.Resources AS r ON r.Id = b.ResourceId;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "AvailabilityWindows",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "BlackoutPeriods",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityWindows_OrgId_ResourceId",
                table: "AvailabilityWindows",
                columns: new[] { "OrgId", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_BlackoutPeriods_OrgId_ResourceId",
                table: "BlackoutPeriods",
                columns: new[] { "OrgId", "ResourceId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AvailabilityWindows_Resources_SameOrg",
                table: "AvailabilityWindows",
                columns: new[] { "OrgId", "ResourceId" },
                principalTable: "Resources",
                principalColumns: new[] { "OrgId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_BlackoutPeriods_Resources_SameOrg",
                table: "BlackoutPeriods",
                columns: new[] { "OrgId", "ResourceId" },
                principalTable: "Resources",
                principalColumns: new[] { "OrgId", "Id" },
                onDelete: ReferentialAction.Cascade);

            // Mechanism 3 for the two new tables. Same shared predicate function
            // as Users/Resources/Bookings — its null-safe branch is inert here,
            // since both columns are NOT NULL.
            migrationBuilder.Sql(
                """
                ALTER SECURITY POLICY Security.TenantAccessPolicy
                    ADD FILTER PREDICATE Security.fn_TenantAccessPredicate(OrgId) ON dbo.AvailabilityWindows,
                    ADD FILTER PREDICATE Security.fn_TenantAccessPredicate(OrgId) ON dbo.BlackoutPeriods;
                """);

            migrationBuilder.Sql("ALTER SECURITY POLICY Security.TenantAccessPolicy WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Predicates first: the policy schema-binds the columns the rest of
            // this method drops.
            migrationBuilder.Sql(
                """
                ALTER SECURITY POLICY Security.TenantAccessPolicy
                    DROP FILTER PREDICATE ON dbo.AvailabilityWindows,
                    DROP FILTER PREDICATE ON dbo.BlackoutPeriods;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AvailabilityWindows_Resources_SameOrg",
                table: "AvailabilityWindows");

            migrationBuilder.DropForeignKey(
                name: "FK_BlackoutPeriods_Resources_SameOrg",
                table: "BlackoutPeriods");

            migrationBuilder.DropIndex(
                name: "IX_AvailabilityWindows_OrgId_ResourceId",
                table: "AvailabilityWindows");

            migrationBuilder.DropIndex(
                name: "IX_BlackoutPeriods_OrgId_ResourceId",
                table: "BlackoutPeriods");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "AvailabilityWindows");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "BlackoutPeriods");

            migrationBuilder.CreateIndex(
                name: "IX_AvailabilityWindows_ResourceId",
                table: "AvailabilityWindows",
                column: "ResourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_AvailabilityWindows_Resources",
                table: "AvailabilityWindows",
                column: "ResourceId",
                principalTable: "Resources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_BlackoutPeriods_Resources",
                table: "BlackoutPeriods",
                column: "ResourceId",
                principalTable: "Resources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
