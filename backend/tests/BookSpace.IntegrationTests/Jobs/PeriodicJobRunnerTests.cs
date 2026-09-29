using BookSpace.Infrastructure.Jobs;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookSpace.IntegrationTests.Jobs;

// WP-8 Phase 1 (docs/wp8-plan.md). Proves PeriodicJobRunner's own contract —
// lease-gated mutual exclusion, crash handover, per-run exception isolation,
// and clean cancellation — against the real JobLeaseRepository and a real
// SQL Server, using fake jobs defined at the bottom of this file. RunOnceAsync
// is called directly rather than waiting on the BackgroundService's real
// timer loop, the same reasoning UserLastAdminGuardConcurrencyTests already
// gives for talking to the repository/unit-of-work layer directly instead of
// hoping wall-clock timing lines up.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class PeriodicJobRunnerTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host;
    private readonly string _jobName = $"wp8-test-{Guid.NewGuid():n}";

    public PeriodicJobRunnerTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    public async Task InitializeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO dbo.JobLeases (JobName, OwnerId, AcquiredAtUtc, LeaseExpiresAtUtc, LastHeartbeatAtUtc)
            VALUES ({_jobName}, '00000000-0000-0000-0000-000000000000', '2000-01-01', '2000-01-01', '2000-01-01')
            """);
    }

    public async Task DisposeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dbo.JobLeases WHERE JobName = {_jobName}");
    }

    [Fact]
    public async Task OnlyOneOfTwoInstancesRunsOnAGivenTick()
    {
        var first = new CountingJob(_host.Services.GetRequiredService<IServiceScopeFactory>(), _jobName, TimeSpan.FromSeconds(30));
        var second = new CountingJob(_host.Services.GetRequiredService<IServiceScopeFactory>(), _jobName, TimeSpan.FromSeconds(30));

        var outcomes = await Task.WhenAll(
            first.RunOnceAsync(CancellationToken.None),
            second.RunOnceAsync(CancellationToken.None));

        Assert.Equal(1, outcomes.Count(o => o.LeaseAcquired));
        Assert.Equal(1, first.RunCount + second.RunCount);
    }

    [Fact]
    public async Task ACrashedOwnersLeaseIsTakenOverByTheNextInstance()
    {
        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();

        // A whole-second lease, not milliseconds: IClock.UtcNow is truncated
        // to the second (SystemClock's own header), so a sub-second duration
        // is not observable — two calls milliseconds apart can read the same
        // truncated instant. One second, with a comfortable margin below
        // before the take-over is checked, keeps this deterministic.
        var leaseDuration = TimeSpan.FromSeconds(1);

        var firstInstance = new CountingJob(scopeFactory, _jobName, leaseDuration);
        var firstOutcome = await firstInstance.RunOnceAsync(CancellationToken.None);
        Assert.True(firstOutcome.LeaseAcquired);
        Assert.Equal(1, firstInstance.RunCount);

        // firstInstance never runs again — simulating a crash. A second
        // instance, immediately after (still well inside the one-second
        // lease), is refused.
        var secondInstance = new CountingJob(scopeFactory, _jobName, TimeSpan.FromSeconds(30));
        var tooSoon = await secondInstance.RunOnceAsync(CancellationToken.None);
        Assert.False(tooSoon.LeaseAcquired);
        Assert.Equal(0, secondInstance.RunCount);

        // Once the first lease has actually expired, the second instance
        // takes over — no job wedged forever because its owner crashed. The
        // 2.5s wait covers the 1s lease plus up to ~1s of truncation slack
        // with margin to spare.
        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        var tookOver = await secondInstance.RunOnceAsync(CancellationToken.None);
        Assert.True(tookOver.LeaseAcquired);
        Assert.Equal(1, secondInstance.RunCount);
    }

    [Fact]
    public async Task AFailingRunIsCaughtAndTheNextTickStillRuns()
    {
        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var job = new ThrowsOnceJob(scopeFactory, _jobName, TimeSpan.FromSeconds(30));

        var failed = await job.RunOnceAsync(CancellationToken.None);
        Assert.True(failed.LeaseAcquired);
        Assert.NotNull(failed.Failure);
        Assert.Null(failed.Summary);

        // The runner caught it rather than propagating — nothing here should
        // have thrown — and the same instance succeeds on its next tick
        // (a renewal of its own lease, not a fresh race).
        var succeeded = await job.RunOnceAsync(CancellationToken.None);
        Assert.True(succeeded.LeaseAcquired);
        Assert.Null(succeeded.Failure);
        Assert.NotNull(succeeded.Summary);
    }

    [Fact]
    public async Task StoppingTheHostedServiceCompletesPromptlyAndDoesNotLeaveARunHalfDone()
    {
        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var job = new CountingJob(scopeFactory, _jobName, TimeSpan.FromSeconds(30))
        {
            PollIntervalOverride = TimeSpan.FromMilliseconds(20),
        };

        await job.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.True(job.RunCount > 0);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await job.StopAsync(stopCts.Token);

        var countAtStop = job.RunCount;
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Stopped means stopped — no further ticks land after StopAsync
        // returns.
        Assert.Equal(countAtStop, job.RunCount);
    }

    // ---- Fakes ----

    private sealed class CountingJob : PeriodicJobRunner
    {
        private readonly string _jobName;
        private readonly TimeSpan _leaseDuration;

        public int RunCount;
        public TimeSpan PollIntervalOverride = TimeSpan.FromMilliseconds(50);

        public CountingJob(IServiceScopeFactory scopeFactory, string jobName, TimeSpan leaseDuration)
            : base(scopeFactory, NullLogger.Instance)
        {
            _jobName = jobName;
            _leaseDuration = leaseDuration;
        }

        protected override string JobName => _jobName;
        protected override TimeSpan PollInterval => PollIntervalOverride;
        protected override TimeSpan LeaseDuration => _leaseDuration;

        protected override Task<JobRunSummary> RunAsync(
            IServiceProvider scopedServices, DateTime nowUtc, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RunCount);
            return Task.FromResult(new JobRunSummary(1, 1, 0));
        }
    }

    // Throws exactly once, then behaves like CountingJob — proves a bad run
    // is isolated to its own tick rather than wedging every run after it.
    private sealed class ThrowsOnceJob : PeriodicJobRunner
    {
        private readonly string _jobName;
        private readonly TimeSpan _leaseDuration;
        private bool _hasThrown;

        public ThrowsOnceJob(IServiceScopeFactory scopeFactory, string jobName, TimeSpan leaseDuration)
            : base(scopeFactory, NullLogger.Instance)
        {
            _jobName = jobName;
            _leaseDuration = leaseDuration;
        }

        protected override string JobName => _jobName;
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(50);
        protected override TimeSpan LeaseDuration => _leaseDuration;

        protected override Task<JobRunSummary> RunAsync(
            IServiceProvider scopedServices, DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (!_hasThrown)
            {
                _hasThrown = true;
                throw new InvalidOperationException("Simulated failure on the first run.");
            }

            return Task.FromResult(new JobRunSummary(1, 1, 0));
        }
    }
}
