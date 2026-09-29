using System.Diagnostics;
using BookSpace.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookSpace.Infrastructure.Jobs;

// WP-8 Phase 1 (docs/wp8-plan.md). The one hosted-service base every
// background job builds on, so the fresh-scope-per-run, cancellation,
// per-run correlation id, structured run-summary logging, per-run exception
// isolation, and lease acquisition are all written once rather than copied
// three times.
//
// A hosted service is registered as a singleton, so it must never hold a
// scoped dependency (a DbContext, a repository) directly — RunAsync is
// always called with a fresh IServiceProvider scope, resolved and disposed
// once per tick, never held across ticks.
//
// RunOnceAsync is public and separate from the BackgroundService timer loop
// specifically so a test can call it directly and assert on its returned
// JobRunOutcome, without waiting on real wall-clock polling — the same
// reasoning UserLastAdminGuardConcurrencyTests already gives for talking to
// the repository/unit-of-work layer directly instead of firing HTTP requests
// and hoping timing lines up.
public abstract class PeriodicJobRunner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    // One id per runner *instance*, not per run — this is what "owner" means
    // in dbo.JobLeases: the same process/instance renews the same lease tick
    // after tick, and a fresh Guid only appears when a new instance starts.
    private readonly Guid _ownerId = Guid.NewGuid();

    protected PeriodicJobRunner(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // The name this job's row in dbo.JobLeases is seeded under
    // (AddJobLeases migration) — must match exactly, or every acquire
    // attempt fails forever against a row that doesn't exist.
    protected abstract string JobName { get; }

    protected abstract TimeSpan PollInterval { get; }

    // How long a won lease is good for before another instance may take it
    // over. Must comfortably exceed how long one run can take, or a slow run
    // loses its own lease mid-flight and a second instance starts processing
    // the same batch concurrently — the multi-instance guarantee this exists
    // to provide would then be the thing that breaks it.
    //
    // Must also be at least a few whole seconds: IClock.UtcNow is truncated to
    // the second (CLAUDE.md §4.3, SystemClock's own header), so a
    // sub-second-scale duration is not observable at all — two calls a few
    // milliseconds apart can read the identical truncated instant, at which
    // point "expires in 200ms" and "expires now" become indistinguishable.
    // Found exactly this way while writing PeriodicJobRunnerTests.
    protected abstract TimeSpan LeaseDuration { get; }

    // The job's actual work. Receives the scope's own IServiceProvider (for
    // resolving scoped repositories/DbContext) and the clock's current time,
    // read once per run so every row this run touches is judged against the
    // same instant.
    protected abstract Task<JobRunSummary> RunAsync(
        IServiceProvider scopedServices,
        DateTime nowUtc,
        CancellationToken cancellationToken);

    public async Task<JobRunOutcome> RunOnceAsync(CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("n");
        using var scope = _scopeFactory.CreateScope();

        using var _ = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["JobName"] = JobName,
        });

        var leases = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        bool acquired;
        try
        {
            acquired = await leases.TryAcquireOrRenewAsync(
                JobName, _ownerId, LeaseDuration, clock.UtcNow, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "{JobName} failed to acquire its lease", JobName);
            return new JobRunOutcome(false, null, ex);
        }

        if (!acquired)
        {
            _logger.LogDebug("{JobName} lease held by another instance this tick", JobName);
            return new JobRunOutcome(false, null, null);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var summary = await RunAsync(scope.ServiceProvider, clock.UtcNow, cancellationToken);
            stopwatch.Stop();

            _logger.LogInformation(
                "{JobName} run complete: picked up {PickedUp}, succeeded {Succeeded}, failed {Failed}, in {ElapsedMs}ms",
                JobName, summary.PickedUp, summary.Succeeded, summary.Failed, stopwatch.ElapsedMilliseconds);

            return new JobRunOutcome(true, summary, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One run failing outright must not take the hosted service down
            // for the rest of the app's lifetime (WP-8's own wording) — caught
            // here, logged, and the loop below simply tries again next tick.
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "{JobName} run threw after {ElapsedMs}ms and was caught by the runner",
                JobName, stopwatch.ElapsedMilliseconds);

            return new JobRunOutcome(true, null, ex);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: the app asked to stop while a delay (or a
            // run that itself honours the token) was in progress. Nothing was
            // left half-written — RunOnceAsync's own try/catch only swallows
            // job-logic exceptions, never cancellation, so a run in progress
            // when this fires has already unwound cleanly.
        }

        // Best-effort — a failure here is not fatal, since an unreleased
        // lease still expires on its own (LeaseDuration is never infinite).
        // This only shortens how long a peer instance waits before it can
        // take over.
        using var scope = _scopeFactory.CreateScope();
        try
        {
            var leases = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            await leases.ReleaseAsync(JobName, _ownerId, clock.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{JobName} could not release its lease on shutdown; it will expire on its own", JobName);
        }
    }
}
