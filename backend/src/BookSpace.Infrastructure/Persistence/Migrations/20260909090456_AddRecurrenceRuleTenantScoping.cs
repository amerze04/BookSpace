using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Decision 0025 (docs/decisions/0025-recurrence-rule-tenant-scoping.md):
    // RecurrenceRules was reachable by id alone, outside all three CLAUDE.md
    // §4.2 isolation mechanisms — the same gap WP-3's 20260831085008 migration
    // closed for AvailabilityWindows and BlackoutPeriods (decision 0014),
    // found here while building WP-5 Phase 2's cancel endpoint, the first
    // thing that ever loads a RecurrenceRule by id rather than only creating
    // one scoped by the resource it belongs to.
    //
    // Hand-edited after scaffolding, in the same three ways
    // 20260831085008_AddChildTableTenantScoping was:
    //
    //  1. The scaffold added OrgId as NOT NULL DEFAULT
    //     '00000000-0000-0000-0000-000000000000', which would stamp any
    //     existing row (the seeded weekly standup) with an OrgId no
    //     Organization owns. Instead: add it nullable, backfill from the
    //     owning Resource, then ALTER to NOT NULL.
    //
    //  2. The backfill reads dbo.Resources, which Security.TenantAccessPolicy
    //     already filters, and TenantSessionContextInterceptor's
    //     @read_only = 1 means this connection cannot grant itself a bypass
    //     mid-session — so the policy is switched off for the duration of the
    //     migration and back on at the end, exactly as before.
    //
    //  3. The new filter predicate is added last, after the ALTER COLUMN —
    //     a filter predicate schema-binds the policy to the column, and SQL
    //     Server will not let ALTER COLUMN touch a column a security policy
    //     already references.
    public partial class AddRecurrenceRuleTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER SECURITY POLICY Security.TenantAccessPolicy WITH (STATE = OFF);");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurrenceRules_Resources",
                table: "RecurrenceRules");

            // EF's own FK index, superseded by IX_RecurrenceRules_OrgId_ResourceId
            // below.
            migrationBuilder.DropIndex(
                name: "IX_RecurrenceRules_ResourceId",
                table: "RecurrenceRules");

            migrationBuilder.AddColumn<Guid>(
                name: "OrgId",
                table: "RecurrenceRules",
                type: "uniqueidentifier",
                nullable: true);

            // Backfill: the owning resource is the only authority on which
            // tenant one of these rows belongs to. Every row has one
            // (ResourceId was already NOT NULL), so no row is left NULL for
            // the ALTER below.
            migrationBuilder.Sql(
                """
                UPDATE rr
                SET rr.OrgId = r.OrgId
                FROM dbo.RecurrenceRules AS rr
                INNER JOIN dbo.Resources AS r ON r.Id = rr.ResourceId;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrgId",
                table: "RecurrenceRules",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceRules_OrgId_ResourceId",
                table: "RecurrenceRules",
                columns: new[] { "OrgId", "ResourceId" });

            migrationBuilder.AddForeignKey(
                name: "FK_RecurrenceRules_Resources_SameOrg",
                table: "RecurrenceRules",
                columns: new[] { "OrgId", "ResourceId" },
                principalTable: "Resources",
                principalColumns: new[] { "OrgId", "Id" });

            // Mechanism 3. Same shared predicate function as the other five
            // tenant-owned tables.
            migrationBuilder.Sql(
                """
                ALTER SECURITY POLICY Security.TenantAccessPolicy
                    ADD FILTER PREDICATE Security.fn_TenantAccessPredicate(OrgId) ON dbo.RecurrenceRules;
                """);

            migrationBuilder.Sql("ALTER SECURITY POLICY Security.TenantAccessPolicy WITH (STATE = ON);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Predicate first: the policy schema-binds the column the rest of
            // this method drops.
            migrationBuilder.Sql(
                """
                ALTER SECURITY POLICY Security.TenantAccessPolicy
                    DROP FILTER PREDICATE ON dbo.RecurrenceRules;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_RecurrenceRules_Resources_SameOrg",
                table: "RecurrenceRules");

            migrationBuilder.DropIndex(
                name: "IX_RecurrenceRules_OrgId_ResourceId",
                table: "RecurrenceRules");

            migrationBuilder.DropColumn(
                name: "OrgId",
                table: "RecurrenceRules");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceRules_ResourceId",
                table: "RecurrenceRules",
                column: "ResourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_RecurrenceRules_Resources",
                table: "RecurrenceRules",
                column: "ResourceId",
                principalTable: "Resources",
                principalColumn: "Id");
        }
    }
}
