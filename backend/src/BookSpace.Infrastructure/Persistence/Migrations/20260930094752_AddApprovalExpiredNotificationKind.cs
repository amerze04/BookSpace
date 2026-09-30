using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // WP-8 Phase 4 (docs/wp8-plan.md, decision D3). Widens CK_Notifications_Kind
    // so 'ApprovalExpired' is a valid stored value — a stale-approval expiry
    // gets its own notification kind rather than reusing 'Rejected', so the
    // notification explaining a Pending request's fate stays distinct from a
    // human's decision. A strict widening: every row that satisfied the
    // constraint before still does, same pattern as AddApprovalDecisionWithdrawn.
    public partial class AddApprovalExpiredNotificationKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications",
                sql: "[Kind] IN ('Confirmed','Rejected','Cancelled','Reminder','ApprovalRequested','NoShowReleased','RecurrenceOccurrenceSkipped','SeriesCancelled','ApprovalExpired')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications",
                sql: "[Kind] IN ('Confirmed','Rejected','Cancelled','Reminder','ApprovalRequested','NoShowReleased','RecurrenceOccurrenceSkipped','SeriesCancelled')");
        }
    }
}
