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
    public async Task ExactlyOneOfTwoConcurrentAcquiresSucceeds()
    {
        var now = DateTime.UtcNow;

        var first = AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), now);
        var second = AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), now);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(acquired => acquired));
    }

    [Fact]
    public async Task TheSameOwnerRenewsRatherThanRacingItself()
    {
        var owner = Guid.NewGuid();
        var now = DateTime.UtcNow;

        Assert.True(await AcquireAsync(owner, TimeSpan.FromSeconds(30), now));
        // Same owner, a later tick: a renewal, not a fresh race.
        Assert.True(await AcquireAsync(owner, TimeSpan.FromSeconds(30), now.AddSeconds(5)));
    }

    [Fact]
    public async Task AnotherOwnerIsRefusedWhileTheLeaseIsStillLive()
    {
        var now = DateTime.UtcNow;

        Assert.True(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), now));
        Assert.False(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), now.AddSeconds(5)));
    }

    [Fact]
    public async Task ACrashedOwnersLeaseIsTakenOverOnceItExpires()
    {
        var now = DateTime.UtcNow;

        // Owner A acquires with a short lease and never renews or releases —
        // simulating an instance that dies mid-run.
        Assert.True(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(1), now));

        // Owner B tries before that lease has expired: refused.
        Assert.False(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), now.AddMilliseconds(500)));

        // Owner B tries again once the clock has moved past the expiry: takes
        // over — no job wedged forever because its owner crashed.
        Assert.True(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromSeconds(30), now.AddSeconds(2)));
    }

    [Fact]
    public async Task ReleaseLetsAnotherOwnerAcquireImmediately()
    {
        var owner = Guid.NewGuid();
        var now = DateTime.UtcNow;

        Assert.True(await AcquireAsync(owner, TimeSpan.FromMinutes(5), now));
        await ReleaseAsync(owner, now.AddSeconds(1));

        // Without the release this would stay refused for the rest of the
        // five-minute lease.
        Assert.True(await AcquireAsync(Guid.NewGuid(), TimeSpan.FromMinutes(5), now.AddSeconds(2)));
    }

    private async Task<bool> AcquireAsync(Guid ownerId, TimeSpan leaseDuration, DateTime nowUtc)
    {
        await using var scope = _host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();
        return await repository.TryAcquireOrRenewAsync(_jobName, ownerId, leaseDuration, nowUtc, CancellationToken.None);
    }

    private async Task ReleaseAsync(Guid ownerId, DateTime nowUtc)
    {
        await using var scope = _host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();
        await repository.ReleaseAsync(_jobName, ownerId, nowUtc, CancellationToken.None);
    }
}
