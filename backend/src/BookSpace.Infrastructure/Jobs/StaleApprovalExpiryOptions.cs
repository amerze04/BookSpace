using System.ComponentModel.DataAnnotations;

namespace BookSpace.Infrastructure.Jobs;

// WP-8 Phase 4 (docs/wp8-plan.md, FR-9.3). Same shape as
// NoShowReleaseOptions and for the same reason: expiry either applies
// (ApprovalRequest.ExpiresAtUtc has elapsed) or it doesn't, and a lost race
// with a concurrent decision is resolved once, inside
// IStaleApprovalExpiryRepository.TryExpireAsync, never retried by this job —
// no retry/backoff knobs needed. Bound with sane defaults (a missing
// "Jobs:StaleApprovalExpiry" section is not a boot failure), but still
// ValidateOnStart so a nonsensical override fails the boot rather than
// spinning the job in a tight loop.
public sealed class StaleApprovalExpiryOptions
{
    public const string SectionName = "Jobs:StaleApprovalExpiry";

    [Range(1, int.MaxValue)]
    public int PollSeconds { get; set; } = 60;

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    // Comfortably longer than a batch of this size should ever take to
    // expire — see PeriodicJobRunner.LeaseDuration's own header for why
    // this also has to clear IClock's whole-second truncation with real
    // margin.
    [Range(1, int.MaxValue)]
    public int LeaseSeconds { get; set; } = 120;
}
