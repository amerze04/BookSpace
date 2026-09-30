using BookSpace.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Jobs;

// WP-8 Phase 3 (docs/wp8-plan.md), FR-9.1, decision 0004. Sweeps every
// Confirmed, not-yet-checked-in booking whose own organisation's
// NoShowGraceMinutes has elapsed, releases it (Booking.MarkNoShow), and
// queues the NoShowReleased notification telling its owner the slot is
// bookable again — picked up and actually sent by NotificationDispatchJob
// (Phase 2), not by this job.
public sealed class NoShowReleaseJob : PeriodicJobRunner
{
    private readonly IOptionsMonitor<NoShowReleaseOptions> _options;

    public NoShowReleaseJob(
        IServiceScopeFactory scopeFactory,
        ILogger<NoShowReleaseJob> logger,
        IOptionsMonitor<NoShowReleaseOptions> options)
        : base(scopeFactory, logger)
    {
        _options = options;
    }

    // Matches the seeded dbo.JobLeases row (AddJobLeases) exactly.
    protected override string JobName => "NoShowRelease";

    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(_options.CurrentValue.PollSeconds);

    protected override TimeSpan LeaseDuration => TimeSpan.FromSeconds(_options.CurrentValue.LeaseSeconds);

    protected override async Task<JobRunSummary> RunAsync(
        IServiceProvider scopedServices, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var noShows = scopedServices.GetRequiredService<INoShowReleaseRepository>();

        var candidates = await noShows.FindNoShowCandidatesAsync(options.BatchSize, nowUtc, cancellationToken);

        var succeeded = 0;
        var failed = 0;

        foreach (var booking in candidates)
        {
            try
            {
                // TryReleaseAsync's own false is not a failure — a
                // concurrent check-in on this specific booking between the
                // read above and this call is exactly the case check-in
                // exists to make possible (decision D5), and this booking is
                // simply no longer a no-show.
                await noShows.TryReleaseAsync(booking, nowUtc, cancellationToken);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // "Isolate failure per item" (WP-8's own wording): one bad
                // booking must not abort every other one still queued
                // behind it in this batch.
                failed++;
            }
        }

        return new JobRunSummary(candidates.Count, succeeded, failed);
    }
}
