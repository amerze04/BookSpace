using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookSpace.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // CLAUDE.md §4.2, mechanism 3: SQL Server row-level security. Hand-written
    // — query filters and interceptors are code, not schema, so
    // `dotnet ef migrations add` scaffolds an empty Up/Down here, same
    // convention as the stored-procedure migrations (CLAUDE.md §5).
    //
    // One predicate function serves Users, Resources, and Bookings: the extra
    // null-safe branch is a no-op for Resources/Bookings (OrgId NOT NULL) and
    // is only load-bearing for Users (OrgId NULL = SysAdmin). TenantInit must
    // be 1 before anything else is evaluated — without it, a null-OrgId
    // SysAdmin row and a connection nobody ever called
    // sp_set_session_context on (a raw ad hoc query, a bug) are
    // indistinguishable to a naive null-safe predicate, which would leak
    // SysAdmin Users rows to any uninitialized connection.
    //
    // Filter predicate only, deliberately no block predicate: a block
    // predicate would need to special-case the two sanctioned no-tenant-context
    // paths (SeedData's inserts, and login before a tenant exists) to avoid
    // breaking them, and that benefit is already covered by the stored
    // procedure gate (§4.1, Bookings) and the SaveChanges* validation
    // (§4.2 mechanism 2, Users/Resources). Revisit once a real
    // SysAdmin/TenantAdmin provisioning write path exists to design it
    // against a concrete flow instead of a guess.
    //
    // See docs/decisions/0013-tenant-isolation-mechanism.md.
    //
    // **Hardening-pass review, 2026-09-11: reconfirmed, not changed.** An
    // independent review asked whether block predicates should be added now.
    // Re-checked against current code rather than re-guessed: SeedData
    // (src/BookSpace.Infrastructure/Persistence/SeedData.cs) and
    // AuthenticationUserRepository both already enter TenantBypassScope,
    // which is the *same* TenantBypass=1 flag a block predicate would also
    // have to check — so both named exceptions above are already covered by
    // one mechanism, not two. The full write-side trust boundary as it
    // stands: (1) BookSpaceDbContext.SaveChangesAsync validates every
    // ITenantOwned entity's OrgId against ICurrentTenant before it can be
    // added or modified (§4.2 mechanism 2); (2) dbo.CreateBooking/
    // dbo.ApproveBooking read Resources through the RLS-filtered table before
    // deciding anything, so a connection with no session context fails
    // closed (decision 0023) rather than relying on a block predicate to
    // stop its own INSERT/UPDATE; (3) decision 0017's raw-SQL test fixtures
    // are test-only, set a real per-row OrgId session context before
    // writing, and are the one place CLAUDE.md §4.1 itself carves out as
    // exempt from the stored-procedure gate. Nothing outside those three
    // writes to a tenant-owned table. Still deliberately filter-only: adding
    // block predicates today would be validated against these three flows as
    // a guess, not against the real SysAdmin/TenantAdmin provisioning write
    // path this comment already said to wait for — that path still does not
    // exist, so the original reasoning stands unchanged.
    public partial class AddTenantIsolationRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA [Security];");

            migrationBuilder.Sql(
                """
                CREATE FUNCTION Security.fn_TenantAccessPredicate(@OrgId UNIQUEIDENTIFIER)
                RETURNS TABLE
                WITH SCHEMABINDING
                AS
                RETURN SELECT 1 AS fn_result
                WHERE CAST(SESSION_CONTEXT(N'TenantInit') AS BIT) = 1
                  AND (
                        CAST(SESSION_CONTEXT(N'TenantBypass') AS BIT) = 1
                        OR @OrgId = CAST(SESSION_CONTEXT(N'OrgId') AS UNIQUEIDENTIFIER)
                        OR (@OrgId IS NULL AND CAST(SESSION_CONTEXT(N'OrgId') AS UNIQUEIDENTIFIER) IS NULL)
                      );
                """);

            migrationBuilder.Sql(
                """
                CREATE SECURITY POLICY Security.TenantAccessPolicy
                    ADD FILTER PREDICATE Security.fn_TenantAccessPredicate(OrgId) ON dbo.Users,
                    ADD FILTER PREDICATE Security.fn_TenantAccessPredicate(OrgId) ON dbo.Resources,
                    ADD FILTER PREDICATE Security.fn_TenantAccessPredicate(OrgId) ON dbo.Bookings
                    WITH (STATE = ON);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: the function is schema-bound to the policy.
            migrationBuilder.Sql("DROP SECURITY POLICY Security.TenantAccessPolicy;");
            migrationBuilder.Sql("DROP FUNCTION Security.fn_TenantAccessPredicate;");
            migrationBuilder.Sql("DROP SCHEMA [Security];");
        }
    }
}
