namespace BookSpace.Application.Abstractions;

// WP-8 Phase 1 (docs/wp8-plan.md, decisions D1/D2). Multi-instance safety for
// the three background jobs: only one running instance may own a given job
// name at a time. Backed by a single row per job name in dbo.JobLeases, never
// read through this application's normal query layer — see JobLeaseRepository
// for why that table has no EF entity at all.
public interface IJobLeaseRepository
{
    // Atomic compare-and-swap: succeeds if the caller already owns the lease
    // (a renewal) or the existing lease has expired (a fresh acquire,
    // including taking over from a crashed owner). Returns false if another
    // owner currently holds an unexpired lease. Never throws for "somebody
    // else has it" — that is the ordinary, expected outcome for every
    // instance but the one that wins a given tick.
    Task<bool> TryAcquireOrRenewAsync(
        string jobName,
        Guid ownerId,
        TimeSpan leaseDuration,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    // Best-effort early release on a clean shutdown, so a stopped instance's
    // peers don't wait out the rest of a lease nobody is renewing any more.
    // Not required for correctness — an unreleased lease still expires on its
    // own — only for a faster handover than that would give.
    Task ReleaseAsync(
        string jobName,
        Guid ownerId,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
