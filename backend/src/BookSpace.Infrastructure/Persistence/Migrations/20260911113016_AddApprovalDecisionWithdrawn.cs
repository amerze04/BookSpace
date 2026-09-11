using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Hardening pass, P2. Widens CK_ApprovalRequests_Decision so 'Withdrawn'
    // is a valid stored value — the invariant this establishes is "a booking
    // no longer Pending cannot have an actionable Pending ApprovalRequest".
    // A strict widening: every row that satisfied the constraint before still
    // does, same pattern as decision 0026's Notifications widening.
    public partial class AddApprovalDecisionWithdrawn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ApprovalRequests_Decision",
                table: "ApprovalRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ApprovalRequests_Decision",
                table: "ApprovalRequests",
                sql: "[Decision] IN ('Pending','Approved','Rejected','Expired','Withdrawn')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ApprovalRequests_Decision",
                table: "ApprovalRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ApprovalRequests_Decision",
                table: "ApprovalRequests",
                sql: "[Decision] IN ('Pending','Approved','Rejected','Expired')");
        }
    }
}
