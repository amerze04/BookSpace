namespace BookSpace.Application.Abstractions;

// WP-8 Phase 1 (docs/wp8-plan.md, decisions D1/D2). Multi-instance safety for
// the three background jobs: only one running instance may own a given job
// name at a time. Backed by a single row per job name in dbo.JobLeases, never
// read through this application's normal query layer — see JobLeaseRepository
// for why that table has no EF entity at all.
//
// Hardening pass, 2026-10 (finding 1): **deliberately takes no `nowUtc`
// parameter.** A distributed lease's correctness boundary has to be one
// shared clock — if two application instances each supplied their own
// `IClock.UtcNow`, an instance whose clock runs behind could write an
// `LeaseExpiresAtUtc` that a second, correctly-clocked instance already reads
// as expired, and the two would then both believe they hold the lease at
// once. The implementation computes and compares every lease timestamp
// against SQL Server's own `SYSUTCDATETIME()`, so lease ownership is
// adjudicated by one authority regardless of how skewed any given host's
// system clock is. This is narrower than fixing clock skew everywhere in the
// application: `IClock` remains the source for every *business* timestamp
// (booking times, notification schedules, §4.3's whole-second-truncation
// convention) — only lease bookkeeping, whose entire job is cross-instance
// coordination, needs a single shared clock.
public interface IJobLeaseRepository
{
    // Atomic compare-and-swap: succeeds if the caller already owns the lease
    // (a renewal) or the existing lease has expired (a fresh acquire,
    // including taking over from a crashed owner). Returns null if another
    // owner currently holds an unexpired lease — never throws for that,
    // since it is the ordinary, expected outcome for every instance but the
    // one that wins a given tick.
    //
    // Returns the *current* `LeaseExpiresAtUtc` (as SQL Server computed it,
    // not as any caller guessed) on success — logged rather than acted on
    // today, but it is the honest value to hand back given the lease clock
    // is the database's, not the caller's.
    Task<DateTime?> TryAcquireOrRenewAsync(
        string jobName,
        Guid ownerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    // Best-effort early release on a clean shutdown, so a stopped instance's
    // peers don't wait out the rest of a lease nobody is renewing any more.
    // Not required for correctness — an unreleased lease still expires on its
    // own — only for a faster handover than that would give.
    Task ReleaseAsync(
        string jobName,
        Guid ownerId,
        CancellationToken cancellationToken);

    // Hardening pass, finding 9: a job name whose seeded dbo.JobLeases row is
    // missing (a typo, a migration that renamed one job's row but not every
    // reference to its old name — this branch's own ReminderDispatch ->
    // NotificationDispatch rename is exactly the kind of drift that can
    // happen) must not look identical to "another instance currently owns
    // it". TryAcquireOrRenewAsync's own zero-rows-affected result cannot
    // distinguish the two, so callers that need to tell them apart — startup
    // validation, specifically — ask directly instead.
    Task<IReadOnlyCollection<string>> FindMissingJobNamesAsync(
        IReadOnlyCollection<string> knownJobNames, CancellationToken cancellationToken);
}
