using System.Data;
using BookSpace.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// WP-8 Phase 1 (docs/wp8-plan.md, decisions D1/D2). dbo.JobLeases has no EF
// entity and no DbSet — nothing in this application ever reads a lease back
// through LINQ, so mapping one would be dead code. The whole contract is one
// atomic UPDATE and its rows-affected count, in the same spirit as the
// stored procedures CLAUDE.md §4.1 reserves for a heavier problem than this
// one: a lease is a single row, so UPDLOCK/HOLDLOCK on it via a plain
// parameterised UPDATE gives the same guarantee a procedure would, with less
// ceremony.
//
// Hardening pass, 2026-10 (finding 1): every timestamp here is
// `SYSUTCDATETIME()`, the database's own clock — never a value supplied by
// the caller. See IJobLeaseRepository's own header for why a distributed
// lease cannot be judged against any one host's `IClock`. This is why
// `TryAcquireOrRenewAsync` switched from `ExecuteSqlInterpolatedAsync`
// (rows-affected only) to a raw `SqlCommand` with an `OUTPUT` clause: reading
// back the expiry SQL Server actually computed, rather than a value this
// process guessed, is the entire point of the fix.
internal sealed class JobLeaseRepository : IJobLeaseRepository
{
    private readonly BookSpaceDbContext _context;

    public JobLeaseRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public async Task<DateTime?> TryAcquireOrRenewAsync(
        string jobName,
        Guid ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

            // WITH (UPDLOCK, HOLDLOCK): the same two hints dbo.CreateBooking
            // takes on its overlap read, scaled down to a single row. UPDLOCK
            // makes two concurrent callers block each other rather than both
            // reading the row as available and both writing; HOLDLOCK holds
            // it to the end of this statement so the second caller's WHERE
            // re-evaluates against the first caller's committed row, not a
            // stale read. The row always exists (seeded by the AddJobLeases
            // migration for every known job name), so there is no
            // insert-if-missing branch to race on — see
            // FindMissingJobNamesAsync for the case where it does not.
            //
            // Acquired when the caller already owns it (a renewal —
            // heartbeat, AcquiredAtUtc untouched) or the existing lease has
            // expired (a fresh acquire, including taking over from a crashed
            // owner). Anyone else holding an unexpired lease matches neither
            // half of the WHERE and this affects zero rows. Every comparison
            // and every written timestamp is SYSUTCDATETIME() — see this
            // file's own header.
            command.CommandText =
                """
                UPDATE dbo.JobLeases WITH (UPDLOCK, HOLDLOCK)
                SET OwnerId = @OwnerId,
                    AcquiredAtUtc = CASE WHEN OwnerId = @OwnerId THEN AcquiredAtUtc ELSE SYSUTCDATETIME() END,
                    LeaseExpiresAtUtc = DATEADD(SECOND, @LeaseDurationSeconds, SYSUTCDATETIME()),
                    LastHeartbeatAtUtc = SYSUTCDATETIME()
                OUTPUT inserted.LeaseExpiresAtUtc
                WHERE JobName = @JobName
                  AND (OwnerId = @OwnerId OR LeaseExpiresAtUtc <= SYSUTCDATETIME())
                """;

            command.Parameters.Add(new SqlParameter("@OwnerId", SqlDbType.UniqueIdentifier) { Value = ownerId });
            command.Parameters.Add(new SqlParameter("@LeaseDurationSeconds", SqlDbType.Int)
            {
                // Whole seconds only — DATEADD's SECOND unit has no finer
                // grain, and datetime2(0)'s own column precision would
                // silently discard anything smaller anyway.
                Value = (int)leaseDuration.TotalSeconds,
            });
            command.Parameters.Add(new SqlParameter("@JobName", SqlDbType.NVarChar, 100) { Value = jobName });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return reader.GetDateTime(0);
        }
        finally
        {
            if (openedHere)
            {
                await _context.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task ReleaseAsync(
        string jobName,
        Guid ownerId,
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
            SET LeaseExpiresAtUtc = SYSUTCDATETIME()
            WHERE JobName = {jobName} AND OwnerId = {ownerId}
            """,
            cancellationToken);
    }

    // Hardening pass, finding 9. Reads every row rather than parameterising
    // an IN clause over a caller-supplied list — dbo.JobLeases has exactly
    // one row per known job (three today), so fetching all of them and
    // computing the set difference in C# is both simpler and just as cheap
    // as building a dynamic WHERE. A plain, unfiltered read — JobLeases
    // carries no tenant column and no query filter (docs/wp8-plan.md,
    // decision D9), so this needs no TenantBypassScope, the same reasoning
    // every other method in this file already relies on.
    public async Task<IReadOnlyCollection<string>> FindMissingJobNamesAsync(
        IReadOnlyCollection<string> knownJobNames, CancellationToken cancellationToken)
    {
        var existing = await _context.Database
            .SqlQuery<string>($"SELECT JobName AS [Value] FROM dbo.JobLeases")
            .ToListAsync(cancellationToken);

        return knownJobNames.Except(existing, StringComparer.Ordinal).ToList();
    }
}
