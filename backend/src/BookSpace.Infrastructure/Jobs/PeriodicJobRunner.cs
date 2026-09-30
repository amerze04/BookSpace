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
    //
    // No longer the only thing standing between a slow run and a lease
    // takeover — see HeartbeatInterval below — but still has to comfortably
    // exceed one heartbeat tick's own round trip, or a slow renew call could
    // outlive the interval meant to precede the next one.
    protected abstract TimeSpan LeaseDuration { get; }

    // How often RunOnceAsync renews the lease *while RunAsync is still in
    // flight*, so a run that outlives LeaseDuration does not lose ownership
    // mid-flight — the gap this class's own LeaseDuration comment used to
    // only warn about rather than close. Half the lease duration is the
    // standard renew-before-expiry safety margin: even if one heartbeat tick
    // is delayed (a GC pause, thread-pool starvation, a slow renew call
    // itself), there is still a second chance before the lease actually
    // expires. Virtual so an unusual job could override it; no job needs to
    // today.
    protected virtual TimeSpan HeartbeatInterval =>
        TimeSpan.FromTicks(LeaseDuration.Ticks / 2);

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

        // Renews the lease in the background for as long as RunAsync is in
        // flight. workCts is what RunAsync actually receives: if a heartbeat
        // is ever refused (another instance already won the lease — this one
        // fell behind by more than LeaseDuration between heartbeats) or
        // throws, exclusive ownership is already gone, and the only correct
        // response is to stop working immediately rather than let RunAsync
        // keep going against a batch a second instance may now also be
        // processing.
        using var heartbeatLoopCts = new CancellationTokenSource();
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeatLoopAsync(workCts, heartbeatLoopCts.Token);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var summary = await RunAsync(scope.ServiceProvider, clock.UtcNow, workCts.Token);
            stopwatch.Stop();

            _logger.LogInformation(
                "{JobName} run complete: picked up {PickedUp}, succeeded {Succeeded}, failed {Failed}, in {ElapsedMs}ms",
                JobName, summary.PickedUp, summary.Succeeded, summary.Failed, stopwatch.ElapsedMilliseconds);

            return new JobRunOutcome(true, summary, null);
        }
        // Cancellation that did *not* come from the caller's own token means
        // the heartbeat lost the lease and cancelled workCts instead — a
        // per-run failure like any other, not app shutdown, so it is caught
        // and reported rather than left to propagate: letting it through
        // uncaught would reach ExecuteAsync's own catch, which only expects
        // stoppingToken to be the source and would otherwise take the whole
        // hosted service down with it (WP-8's own "must never silently kill
        // the hosted service" requirement, applied to this new failure mode
        // too).
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "{JobName} run stopped after {ElapsedMs}ms because its lease was lost mid-flight",
                JobName, stopwatch.ElapsedMilliseconds);

            return new JobRunOutcome(true, null, new OperationCanceledException("Lease lost mid-run."));
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
        finally
        {
            // Stops the loop below regardless of how RunAsync finished —
            // success, a caught failure, or the cancellation case above —
            // and waits for it to actually unwind before this method returns,
            // so no heartbeat renewal is ever still in flight (on this scope,
            // or the next tick's) after RunOnceAsync hands control back.
            heartbeatLoopCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected: this is exactly how the loop below stops.
            }
        }
    }

    // Renews the lease once per HeartbeatInterval until stopLoop is
    // cancelled (RunAsync finished, one way or another). Uses its own fresh
    // DI scope per tick rather than RunOnceAsync's own scope: that scope's
    // DbContext is busy doing RunAsync's actual work concurrently with this
    // loop, and DbContext is not thread-safe.
    private async Task RunHeartbeatLoopAsync(CancellationTokenSource workCts, CancellationToken stopLoop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, stopLoop);

                using var heartbeatScope = _scopeFactory.CreateScope();
                var leases = heartbeatScope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();
                var clock = heartbeatScope.ServiceProvider.GetRequiredService<IClock>();

                bool renewed;
                try
                {
                    renewed = await leases.TryAcquireOrRenewAsync(
                        JobName, _ownerId, LeaseDuration, clock.UtcNow, stopLoop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex, "{JobName} heartbeat failed to renew its lease; stopping the run", JobName);
                    workCts.Cancel();
                    return;
                }

                if (!renewed)
                {
                    _logger.LogWarning(
                        "{JobName} lost its lease to another instance mid-run; stopping the run", JobName);
                    workCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The ordinary path: RunAsync finished and RunOnceAsync's own
            // finally block cancelled stopLoop.
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
