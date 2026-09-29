namespace BookSpace.Infrastructure.Jobs;

// WP-8 (docs/wp8-plan.md, Phase 1). What a single job run reports — "you
// can't operate a job you can't see" is the WP's own framing, and this is
// the shape PeriodicJobRunner logs after every tick.
public sealed record JobRunSummary(int PickedUp, int Succeeded, int Failed)
{
    public static readonly JobRunSummary Empty = new(0, 0, 0);
}

// The outcome of one PeriodicJobRunner.RunOnceAsync call, returned (not just
// logged) specifically so a test can assert on it directly — the same reason
// UserLastAdminGuardConcurrencyTests talks to the repository/unit-of-work
// layer instead of only asserting on logs or side effects.
//
// LeaseAcquired false means "someone else owns it this tick", the ordinary
// outcome for every instance but one; it is not a failure and Failure is
// null in that case. Failure is only set when RunAsync itself threw — the
// per-run isolation the WP asks for ("an unhandled exception in a job... must
// never silently kill the hosted service"), surfaced here rather than
// swallowed silently.
public sealed record JobRunOutcome(bool LeaseAcquired, JobRunSummary? Summary, Exception? Failure);
