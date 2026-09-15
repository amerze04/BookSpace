using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass. Optimistic concurrency on Resources, the same mechanism
    // Bookings.RowVersion already uses (CLAUDE.md §5) — closes the race
    // between UpdateResource (setting RequiresApproval = true) and
    // ReplaceApprovers (clearing the approver list), which previously had no
    // way to detect that the other had run: both are read-check-mutate-save
    // with nothing serializing them, so either could commit against the
    // other's now-stale read. See Resource.RowVersion's own header for the
    // full reasoning and ResourceConcurrencyTests for the deterministic proof.
    public partial class AddResourceRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Resources",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Resources");
        }
    }
}
