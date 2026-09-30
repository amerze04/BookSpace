using BookSpace.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Jobs;

// WP-8 Phase 4 (docs/wp8-plan.md), FR-9.3, decisions D3/D3a. Sweeps every
// Pending ApprovalRequest whose ExpiresAtUtc has elapsed, expires it and
// rejects its booking (Booking.ExpireApproval), and queues the
// ApprovalExpired notification telling its owner why — picked up and
// actually sent by NotificationDispatchJob (Phase 2), not by this job.
public sealed class StaleApprovalExpiryJob : PeriodicJobRunner
{
    private readonly IOptionsMonitor<StaleApprovalExpiryOptions> _options;

    public StaleApprovalExpiryJob(
        IServiceScopeFactory scopeFactory,
        ILogger<StaleApprovalExpiryJob> logger,
        IOptionsMonitor<StaleApprovalExpiryOptions> options)
        : base(scopeFactory, logger)
    {
        _options = options;
    }

    // Matches the seeded dbo.JobLeases row (AddJobLeases) exactly.
    protected override string JobName => "StaleApprovalExpiry";

    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(_options.CurrentValue.PollSeconds);

    protected override TimeSpan LeaseDuration => TimeSpan.FromSeconds(_options.CurrentValue.LeaseSeconds);

    protected override async Task<JobRunSummary> RunAsync(
        IServiceProvider scopedServices, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var expiries = scopedServices.GetRequiredService<IStaleApprovalExpiryRepository>();

        var candidates = await expiries.FindExpiredCandidatesAsync(options.BatchSize, nowUtc, cancellationToken);

        var succeeded = 0;
        var failed = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                // TryExpireAsync's own false is not a failure — a concurrent
                // decision on this specific booking between the read above
                // and this call is exactly the case a human deciding (or
                // dbo.ApproveBooking's own re-check) makes possible, and this
                // request is simply no longer Pending to expire.
                await expiries.TryExpireAsync(candidate, nowUtc, cancellationToken);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // "Isolate failure per item" (WP-8's own wording): one bad
                // candidate must not abort every other one still queued
                // behind it in this batch.
                failed++;
            }
        }

        return new JobRunSummary(candidates.Count, succeeded, failed);
    }
}
