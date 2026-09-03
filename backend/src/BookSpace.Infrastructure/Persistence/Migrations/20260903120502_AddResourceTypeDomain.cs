using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceTypeDomain : Migration
    {
        /// <inheritdoc />
        // ResourceType was a free NVARCHAR(50) until now, so this constraint can
        // only apply if every existing row already holds one of the five names.
        // Verified before applying (2026-09-04): the dev and test databases hold
        // only "Room" and "Equipment", both valid.
        //
        // Deliberately strict rather than normalising unknown values to 'Other'
        // first: on a database this migration has not seen, failing to apply is a
        // better outcome than silently reclassifying a tenant's resources. The fix
        // in that case is a one-off UPDATE whose mapping the owner should choose.
        //
        // The column itself does not change — same type, same length. Only what
        // may go in it.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Resources_ResourceType",
                table: "Resources",
                sql: "[ResourceType] IN ('Room', 'Equipment', 'Vehicle', 'LabSlot', 'Other')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Resources_ResourceType",
                table: "Resources");
        }
    }
}
