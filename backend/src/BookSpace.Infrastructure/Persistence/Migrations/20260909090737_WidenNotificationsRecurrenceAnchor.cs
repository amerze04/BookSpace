using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // Decision 0026: WP-5 Phase 2's whole-series cancel needs a notification
    // anchored to a RecurrenceRule with no single occurrence date to hang it
    // off (SeriesCancelled) — decision #8's original CK_Notifications_HasContext
    // required RecurrenceRuleId *and* OccurrenceDate together, built for its
    // one specific kind (RecurrenceOccurrenceSkipped). This loosens it to
    // "RecurrenceRuleId alone is a valid anchor", which is a superset: every
    // row that satisfied the old constraint still satisfies this one.
    public partial class WidenNotificationsRecurrenceAnchor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_HasContext",
                table: "Notifications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_HasContext",
                table: "Notifications",
                sql: "[BookingId] IS NOT NULL OR [RecurrenceRuleId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications",
                sql: "[Kind] IN ('Confirmed','Rejected','Cancelled','Reminder','ApprovalRequested','NoShowReleased','RecurrenceOccurrenceSkipped','SeriesCancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_HasContext",
                table: "Notifications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_HasContext",
                table: "Notifications",
                sql: "[BookingId] IS NOT NULL OR ([RecurrenceRuleId] IS NOT NULL AND [OccurrenceDate] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_Kind",
                table: "Notifications",
                sql: "[Kind] IN ('Confirmed','Rejected','Cancelled','Reminder','ApprovalRequested','NoShowReleased','RecurrenceOccurrenceSkipped')");
        }
    }
}
