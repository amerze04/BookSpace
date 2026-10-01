using System.ComponentModel.DataAnnotations;

namespace BookSpace.Infrastructure.Jobs;

// WP-8 Phase 3 (docs/wp8-plan.md, FR-9.1, decision 0004). Simpler than
// NotificationDispatchOptions: a no-show sweep has no retry/backoff concept
// of its own — a booking either qualifies (Booking.IsNoShow) or it doesn't,
// and a lost race with a concurrent check-in is resolved once, inside
// INoShowReleaseRepository.TryReleaseAsync, never retried by this job. Same
// posture as its sibling otherwise: bound with sane defaults (a missing
// "Jobs:NoShowRelease" section is not a boot failure), but still
// ValidateOnStart so a nonsensical override fails the boot rather than
// spinning the job in a tight loop.
public sealed class NoShowReleaseOptions
{
    public const string SectionName = "Jobs:NoShowRelease";

    [Range(1, int.MaxValue)]
    public int PollSeconds { get; set; } = 60;

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    // Comfortably longer than a batch of this size should ever take to
    // release — see PeriodicJobRunner.LeaseDuration's own header for why
    // this also has to clear IClock's whole-second truncation with real
    // margin. Floor of 5 rather than 1 (hardening pass, finding 8) — see
    // NotificationDispatchOptions' own comment on the identical fix.
    [Range(5, int.MaxValue)]
    public int LeaseSeconds { get; set; } = 120;
}
