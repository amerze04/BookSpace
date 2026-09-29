using BookSpace.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// WP-8 Phase 1 (docs/wp8-plan.md, decisions D1/D2). dbo.JobLeases has no EF
// entity and no DbSet — nothing in this application ever reads a lease back
// through LINQ, so mapping one would be dead code. The whole contract is one
// atomic UPDATE and its rows-affected count, in the same spirit as the
// stored procedures CLAUDE.md §4.1 reserves for a heavier problem than this
// one: a lease is a single row, so UPDLOCK/HOLDLOCK on it via a plain
// parameterised UPDATE gives the same guarantee a procedure would, with less
// ceremony. ExecuteSqlInterpolatedAsync parameterises every interpolated
// value — this is not string concatenation (CLAUDE.md §5).
internal sealed class JobLeaseRepository : IJobLeaseRepository
{
    private readonly BookSpaceDbContext _context;

    public JobLeaseRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public async Task<bool> TryAcquireOrRenewAsync(
        string jobName,
        Guid ownerId,
        TimeSpan leaseDuration,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var leaseExpiresAtUtc = nowUtc.Add(leaseDuration);

        // WITH (UPDLOCK, HOLDLOCK): the same two hints dbo.CreateBooking takes
        // on its overlap read, scaled down to a single row. UPDLOCK makes two
        // concurrent callers block each other rather than both reading the row
        // as available and both writing; HOLDLOCK holds it to the end of this
        // statement so the second caller's WHERE re-evaluates against the
        // first caller's committed row, not a stale read. The row always
        // exists (seeded by the AddJobLeases migration for every known job
        // name), so there is no insert-if-missing branch to race on.
        //
        // Acquired when the caller already owns it (a renewal — heartbeat,
        // AcquiredAtUtc untouched) or the existing lease has expired (a fresh
        // acquire, including taking over from a crashed owner). Anyone else
        // holding an unexpired lease matches neither half of the WHERE and
        // this affects zero rows.
        var rowsAffected = await _context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE dbo.JobLeases WITH (UPDLOCK, HOLDLOCK)
            SET OwnerId = {ownerId},
                AcquiredAtUtc = CASE WHEN OwnerId = {ownerId} THEN AcquiredAtUtc ELSE {nowUtc} END,
                LeaseExpiresAtUtc = {leaseExpiresAtUtc},
                LastHeartbeatAtUtc = {nowUtc}
            WHERE JobName = {jobName}
              AND (OwnerId = {ownerId} OR LeaseExpiresAtUtc <= {nowUtc})
            """,
            cancellationToken);

        return rowsAffected == 1;
    }

    public async Task ReleaseAsync(
        string jobName,
        Guid ownerId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // Expires the lease immediately rather than deleting the row — the
        // row is permanent (one per known job name, seeded once) and the next
        // acquirer's WHERE already treats an expired lease as available. Only
        // the current owner may release it: a caller that already lost the
        // lease to someone else must not expire *their* lease instead.
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE dbo.JobLeases
            SET LeaseExpiresAtUtc = {nowUtc}
            WHERE JobName = {jobName} AND OwnerId = {ownerId}
            """,
            cancellationToken);
    }
}
