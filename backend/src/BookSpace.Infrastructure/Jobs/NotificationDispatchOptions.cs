using System.ComponentModel.DataAnnotations;

namespace BookSpace.Infrastructure.Jobs;

// WP-8 Phase 2 (docs/wp8-plan.md). "Make the poll interval and batch size
// configurable, not hardcoded" — extended to the retry cap and backoff base,
// since those are exactly as much a deployment tuning knob as the other two.
// Bound with sane defaults (a missing "Jobs:NotificationDispatch" section is
// not a boot failure, unlike EmailOptions/JwtOptions — nothing here can leak
// a secret or silently drop email the way a wrong delivery mode could), but
// still ValidateOnStart so a nonsensical override (zero or negative) fails
// the boot rather than spinning the job in a tight loop or never retrying.
public sealed class NotificationDispatchOptions
{
    public const string SectionName = "Jobs:NotificationDispatch";

    [Range(1, int.MaxValue)]
    public int PollSeconds { get; set; } = 60;

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 50;

    // Comfortably longer than a batch of this size should ever take to send —
    // see PeriodicJobRunner.LeaseDuration's own header for why this also has
    // to clear IClock's whole-second truncation with real margin.
    //
    // Hardening pass, finding 8: the floor used to be [Range(1, ...)], which
    // let a 1-second override through even though this comment (and
    // LeaseDuration's own) already said "comfortably longer" — a real
    // configuration mistake this codebase would have installed with a 400-style
    // validation error at boot for a JSON typo elsewhere, but silently
    // accepted here. Five seconds is comfortably above both the whole-second
    // truncation floor and HeartbeatInterval's own (LeaseSeconds / 2) margin.
    [Range(5, int.MaxValue)]
    public int LeaseSeconds { get; set; } = 120;

    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 5;

    [Range(1, int.MaxValue)]
    public int BackoffBaseSeconds { get; set; } = 30;
}
