using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Jobs;

// WP-8 Phase 1 (docs/wp8-plan.md, decision D1). Proves the compare-and-swap
// UPDATE in JobLeaseRepository does for job ownership what dbo.CreateBooking's
// UPDLOCK/HOLDLOCK does for booking capacity: two concurrent callers racing
// for the same lease produce exactly one winner, never both and never
// neither. Each racing call gets its own DI scope (and so its own
// DbContext/connection) — a DbContext is not thread-safe, and two real
// instances would never share one either.
//
// Uses its own throwaway job name rather than one of the three seeded real
// ones (AddJobLeases migration), so this suite never contends with — or
// leaves a held lease behind for — the actual jobs later phases build.
//
// Hardening pass, 2026-10 (finding 1): every test in this file used to pass
// one shared `DateTime nowUtc` to both contenders — proving the SQL's own
// compare-and-swap logic, but nothing about whether two *independently
// clocked* processes could disagree, because the test itself supplied the
// only clock either side ever saw. `IJobLeaseRepository.TryAcquireOrRenewAsync`
// no longer takes a `nowUtc` parameter at all — see its own header for why —
// so every test here now waits on real elapsed time instead, the same
// technique `PeriodicJobRunnerTests` already uses for its own lease-timing
// tests. That is not a weaker proof: a caller-supplied clock is no longer
// something the interface can even accept, so there is nothing left here for
// two skewed instances to disagree about.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class JobLeaseRepositoryConcurrencyTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host;
    private readonly string _jobName = $"wp8-test-{Guid.NewGuid():n}";

    public JobLeaseRepositoryConcurrencyTests(AuthenticationTestHost host)
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
            VALUES ({_jobName}, '00000000-0000-0000-0000-000000000000', SYSUTCDATETIME(), '2000-01-01', SYSUTCDATETIME())
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
    public async Task ExactlyOneOfTwoConcurrentAcquiresSucceeds()
    {
        var first = AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30));
        var second = AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30));

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(acquired => acquired is not null));
    }

    [Fact]
    public async Task TheSameOwnerRenewsRatherThanRacingItself()
    {
        var owner = Guid.NewGuid();

        Assert.NotNull(await AcquireAsync(owner, TimeSpan.FromSeconds(30)));
        // Same owner, a later tick: a renewal, not a fresh race.
        Assert.NotNull(await AcquireAsync(owner, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task AnotherOwnerIsRefusedWhileTheLeaseIsStillLive()
    {
        Assert.NotNull(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30)));
        Assert.Null(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30)));
    }

    // Whole-second, real elapsed time — IJobLeaseRepository's own clock
    // (SYSUTCDATETIME()) has no sub-second observability concern the way
    // IClock.UtcNow's truncation did, but a short, real lease is still the
    // simplest way to prove a takeover without a long-running test.
    [Fact]
    public async Task ACrashedOwnersLeaseIsTakenOverOnceItExpires()
    {
        // Owner A acquires with a short lease and never renews or releases —
        // simulating an instance that dies mid-run.
        Assert.NotNull(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(1)));

        // Owner B tries immediately, well before that lease has expired:
        // refused.
        Assert.Null(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30)));

        // Once the lease has actually expired in real time, B takes over —
        // no job wedged forever because its owner crashed.
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        Assert.NotNull(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ReleaseLetsAnotherOwnerAcquireImmediately()
    {
        var owner = Guid.NewGuid();

        Assert.NotNull(await AcquireAsync(owner, TimeSpan.FromMinutes(5)));
        await ReleaseAsync(owner);

        // Without the release this would stay refused for the rest of the
        // five-minute lease.
        Assert.NotNull(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5)));
    }

    // Finding 1's central claim, demonstrated rather than merely argued: two
    // "instances" race for the same lease with no shared clock anywhere in
    // the call — each AcquireAsync below resolves its own scope, its own
    // DbContext/connection, and passes no time value at all. Real elapsed
    // time between the two calls is the only clock either side's outcome
    // could possibly depend on, and it is SQL Server's own. A version of
    // this test that (like the pre-hardening-pass tests here) passed one
    // synthetic `now` to both racers would have kept passing even if the
    // implementation still secretly compared against a caller-supplied
    // value — this one cannot, because there is no such value to pass.
    [Fact]
    public async Task TwoIndependentlyClockedCallersStillProduceExactlyOneWinner()
    {
        var winners = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30))));

        Assert.Equal(1, winners.Count(acquired => acquired is not null));
    }

    private async Task<DateTime?> AcquireAsync(Guid ownerId, TimeSpan leaseDuration)
    {
        await using var scope = _host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();
        return await repository.TryAcquireOrRenewAsync(_jobName, ownerId, leaseDuration, CancellationToken.None);
    }

    private async Task ReleaseAsync(Guid ownerId)
    {
        await using var scope = _host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();
        await repository.ReleaseAsync(_jobName, ownerId, CancellationToken.None);
    }
}
