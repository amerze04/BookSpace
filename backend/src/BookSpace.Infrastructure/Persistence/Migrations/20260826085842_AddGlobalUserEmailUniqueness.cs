using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // An email now identifies exactly one user platform-wide, so credential
    // login needs no tenant discriminator — docs/decisions/0010.
    //
    // Will fail loudly on any database already holding the same email in two
    // tenants. That is the intended behavior: a silently skipped unique index
    // would be far worse than a blocked migration.
    public partial class AddGlobalUserEmailUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Users_Org_Email",
                table: "Users");

            // Not incidental: the dropped composite index led on OrgId, so it was
            // also serving FK_Users_Organizations. Losing it would leave that FK
            // without a supporting index, so EF adds a plain one back.
            migrationBuilder.CreateIndex(
                name: "IX_Users_OrgId",
                table: "Users",
                column: "OrgId");

            migrationBuilder.CreateIndex(
                name: "UQ_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_OrgId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "UQ_Users_Email",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "UX_Users_Org_Email",
                table: "Users",
                columns: new[] { "OrgId", "Email" },
                unique: true,
                filter: "[OrgId] IS NOT NULL");
        }
    }
}
