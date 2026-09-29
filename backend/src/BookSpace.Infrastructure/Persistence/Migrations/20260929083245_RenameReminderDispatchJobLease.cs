using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // WP-8 Phase 2. Phase 1's AddJobLeases migration seeded this row under
    // the name WP-8's own task list uses for the job ("Reminder job"), before
    // Phase 2 revealed its actual scope: NotificationDispatchJob sends every
    // notification kind that has ever been queued (Confirmed, Rejected,
    // Cancelled, ApprovalRequested and the two recurrence kinds — not only
    // Reminder), because they all share one Notifications table and one
    // dispatch loop. Renamed here, rather than left inaccurate, so the job
    // name in dbo.JobLeases matches the class and the config section
    // (Jobs:NotificationDispatch) that both already used this name.
    //
    // No EF entity to change (docs/wp8-plan.md decision D2) — hand-written,
    // like AddJobLeases itself.
    public partial class RenameReminderDispatchJobLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE dbo.JobLeases SET JobName = 'NotificationDispatch' WHERE JobName = 'ReminderDispatch';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE dbo.JobLeases SET JobName = 'ReminderDispatch' WHERE JobName = 'NotificationDispatch';");
        }
    }
}
