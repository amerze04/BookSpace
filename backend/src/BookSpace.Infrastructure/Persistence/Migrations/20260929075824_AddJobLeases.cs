using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // WP-8 Phase 1. Hand-written, like the stored-procedure migrations —
    // `dotnet ef migrations add` produced an empty Up/Down here because
    // JobLeases has no EF entity at all (docs/wp8-plan.md, decision D2): the
    // repository only ever runs one atomic UPDATE and reads rows-affected,
    // nothing reads this table through LINQ, so an entity would be dead code —
    // the same reasoning CLAUDE.md §5 already applies to stored procedures and
    // RLS policies.
    //
    // No OrgId, no query filter, no RLS predicate: this is global job-ownership
    // state, not tenant data (docs/wp8-plan.md, decision D9).
    public partial class AddJobLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE dbo.JobLeases (
                    JobName          NVARCHAR(100)    NOT NULL,
                    OwnerId          UNIQUEIDENTIFIER NOT NULL,
                    AcquiredAtUtc    DATETIME2(0)     NOT NULL,
                    LeaseExpiresAtUtc DATETIME2(0)    NOT NULL,
                    LastHeartbeatAtUtc DATETIME2(0)   NOT NULL,
                    CONSTRAINT PK_JobLeases PRIMARY KEY (JobName)
                );
                """);

            // One row per job this package builds, seeded with an
            // already-expired lease and a nil owner so the first real
            // acquire attempt — whichever instance gets there first, in
            // whichever phase wires up that job — succeeds immediately via
            // the ordinary "expired lease" branch of the UPDATE, rather than
            // needing a separate insert-if-missing path in the repository.
            migrationBuilder.Sql(
                """
                INSERT INTO dbo.JobLeases (JobName, OwnerId, AcquiredAtUtc, LeaseExpiresAtUtc, LastHeartbeatAtUtc)
                VALUES
                    ('ReminderDispatch',    '00000000-0000-0000-0000-000000000000', '2000-01-01', '2000-01-01', '2000-01-01'),
                    ('NoShowRelease',       '00000000-0000-0000-0000-000000000000', '2000-01-01', '2000-01-01', '2000-01-01'),
                    ('StaleApprovalExpiry', '00000000-0000-0000-0000-000000000000', '2000-01-01', '2000-01-01', '2000-01-01');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE dbo.JobLeases;");
        }
    }
}
