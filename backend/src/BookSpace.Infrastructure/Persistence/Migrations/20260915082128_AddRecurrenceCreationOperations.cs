using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, item 11. Built tenant-scoped from the start — the
    // query filter is in BookSpaceDbContext.OnModelCreating (mechanism 1),
    // SaveChanges validation covers every ITenantOwned entity already
    // (mechanism 2), and this migration adds mechanism 3: the RLS filter
    // predicate, the same shared function every other tenant-owned table
    // uses. No security-policy OFF/ON toggle needed the way decisions 0014
    // and 0025 required — those retrofitted an existing, already-referenced
    // column; this is a brand-new table with OrgId NOT NULL from its first
    // row, so there is no backfill and nothing already schema-bound to work
    // around.
    public partial class AddRecurrenceCreationOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecurrenceCreationOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrgId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RecurrenceRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(0)", precision: 0, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrenceCreationOperations", x => x.Id);
                    table.CheckConstraint("CK_RecurrenceCreationOperations_Status", "[Status] IN ('Creating','Active','Failed')");
                    table.ForeignKey(
                        name: "FK_RecurrenceCreationOperations_CreatedBy",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecurrenceCreationOperations_Organizations",
                        column: x => x.OrgId,
                        principalTable: "Organizations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecurrenceCreationOperations_UpdatedBy",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RecurrenceCreationOperations_Users",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceCreationOperations_CreatedByUserId",
                table: "RecurrenceCreationOperations",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceCreationOperations_UpdatedByUserId",
                table: "RecurrenceCreationOperations",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceCreationOperations_UserId",
                table: "RecurrenceCreationOperations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UQ_RecurrenceCreationOperations_Org_User_Key",
                table: "RecurrenceCreationOperations",
                columns: new[] { "OrgId", "UserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql(
                """
                ALTER SECURITY POLICY Security.TenantAccessPolicy
                    ADD FILTER PREDICATE Security.fn_TenantAccessPredicate(OrgId) ON dbo.RecurrenceCreationOperations;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER SECURITY POLICY Security.TenantAccessPolicy
                    DROP FILTER PREDICATE ON dbo.RecurrenceCreationOperations;
                """);

            migrationBuilder.DropTable(
                name: "RecurrenceCreationOperations");
        }
    }
}
