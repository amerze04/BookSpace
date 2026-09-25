using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using BookSpace.IntegrationTests.Support;
using BookSpace.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Users;

// **The proof that the last-admin guard's read actually takes a lock.**
//
// Its sibling, UserWriteEndpointTests, fires two real HTTP requests at once and
// passes — but it passes with the `UPDLOCK, HOLDLOCK` hints removed too
// (measured, five runs out of five), because the window between the guard's read
// and its write is too narrow for two TestServer requests to interleave by luck.
// A test that cannot fail when the thing it is about is deleted is not evidence,
// which is the lesson WP-7 Phase 6 wrote down.
//
// So this one forces the interleaving rather than hoping for it, at the level
// the lock actually lives:
//
//   T1  begin ─ count (takes the lock) ─── hold 1.5s ─── write ─ commit
//   T2            begin ─ count ─────────(blocked)────────────── ▲ then reads
//                                                                 the truth
//
// With the hints, T2's count cannot run until T1 commits, so it sees zero other
// active admins and the guard fires. Without them it reads straight past T1's
// uncommitted work, sees one, and both writes land — leaving Acme with no
// administrator at all, which no endpoint in this system can then repair.
//
// The hold is 1.5 seconds against a ~300ms head start: far longer than any
// scheduling jitter, so the ordering is not a race of its own.
//
// It talks to the repository and IUnitOfWork directly rather than over HTTP,
// which is the same shape WP-4 used — a procedure-level concurrency test
// alongside an endpoint-level one (decision `0023`).
[Collection(nameof(AuthenticationTestCollection))]
public sealed class UserLastAdminGuardConcurrencyTests : IAsyncLifetime
{
    private const string AcmeAdmin = "admin@acme.test";

    private static readonly TimeSpan HeadStart = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan HoldTheLock = TimeSpan.FromMilliseconds(1500);

    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _createdUserIds = [];

    private Guid _acmeOrgId;
    private Guid _firstAdminId;
    private Guid _secondAdminId;

    public UserLastAdminGuardConcurrencyTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // Two admins of its own, so the seeded one is never left without its role —
    // several classes in this collection log in as it.
    public async Task InitializeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var seededAdmin = await context.Users
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Email == AcmeAdmin);

        _acmeOrgId = seededAdmin.OrgId!.Value;
        _firstAdminId = await AddAdminAsync(context, seededAdmin.Id);
        _secondAdminId = await AddAdminAsync(context, seededAdmin.Id);
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var users = await context.Users
            .IgnoreQueryFilters()
            .Where(u => _createdUserIds.Contains(u.Id))
            .ToListAsync();

        context.Users.RemoveRange(users);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheSecondReaderWaitsForTheFirstToCommit()
    {
        // The seeded admin is an active TenantAdmin too, so two more makes
        // three. Both writes below are individually legal; the question is what
        // happens when they overlap, so the count each one sees is what matters.
        var before = await CountActiveAdminsAsync();
        Assert.Equal(3, before);

        // Take the seeded admin's role out of the picture for the duration, so
        // the two under test really are the last two. Restored in DisposeAsync's
        // sibling below — done here rather than in InitializeAsync so the
        // arrangement reads in one place.
        await SetSeededAdminRoleAsync(hasRole: false);

        try
        {
            Assert.Equal(2, await CountActiveAdminsAsync());

            var first = RemoveAdminRoleAsync(_firstAdminId, holdFor: HoldTheLock);
            await Task.Delay(HeadStart);
            var second = RemoveAdminRoleAsync(_secondAdminId, holdFor: TimeSpan.Zero);

            var outcomes = await Task.WhenAll(first, second);

            // Exactly one write may land. The other must have been refused —
            // either by the guard (it read the truth after blocking) or, if SQL
            // Server chose it as a deadlock victim, by the guard after the
            // execution strategy retried it.
            Assert.DoesNotContain(outcomes, o => o.NotFound);
            Assert.Equal(1, outcomes.Count(o => o.Succeeded));
            Assert.All(
                outcomes.Where(o => !o.Succeeded),
                o => Assert.True(
                    o.RefusedByGuard || o.Failure is Microsoft.EntityFrameworkCore.DbUpdateException,
                    $"Unexpected failure: {o.Failure}"));

            // **The invariant itself**, which is the assertion that matters:
            // Acme still has an administrator.
            Assert.Equal(1, await CountActiveAdminsAsync());
        }
        finally
        {
            await SetSeededAdminRoleAsync(hasRole: true);
        }
    }

    // The same shape for the other door into the invariant: one admin is
    // deactivated while the other has their role removed.
    [Fact]
    public async Task DeactivationAndRoleRemovalCannotBothLand()
    {
        await SetSeededAdminRoleAsync(hasRole: false);

        try
        {
            Assert.Equal(2, await CountActiveAdminsAsync());

            var first = DeactivateAsync(_firstAdminId, holdFor: HoldTheLock);
            await Task.Delay(HeadStart);
            var second = RemoveAdminRoleAsync(_secondAdminId, holdFor: TimeSpan.Zero);

            var outcomes = await Task.WhenAll(first, second);

            Assert.DoesNotContain(outcomes, o => o.NotFound);
            Assert.Equal(1, outcomes.Count(o => o.Succeeded));
            Assert.Equal(1, await CountActiveAdminsAsync());
        }
        finally
        {
            await ReactivateAsync(_firstAdminId);
            await SetSeededAdminRoleAsync(hasRole: true);
        }
    }

    // ---- The two racing operations ----

    private Task<Outcome> RemoveAdminRoleAsync(Guid userId, TimeSpan holdFor) =>
        RunGuardedWriteAsync(userId, holdFor, (user, now) =>
            user.ReplaceRoles([Role.Member], userId, now));

    private Task<Outcome> DeactivateAsync(Guid userId, TimeSpan holdFor) =>
        RunGuardedWriteAsync(userId, holdFor, (user, now) => user.Deactivate(userId, now));

    // The handlers' own sequence, reproduced with a hold inserted between the
    // guard's read and the write: resolve the user, count the *other* active
    // admins under the lock, refuse if there are none, otherwise write — all
    // inside one IUnitOfWork.ExecuteAsync, which is the part being tested.
    //
    // **The stack is built by hand rather than resolved from the host's DI, and
    // that is not laziness.** `ICurrentTenant` is `HttpContextCurrentTenant`,
    // which reads claims off the current request — outside one it answers null,
    // and a null tenant means the query filter matches no Acme row and RLS hides
    // every one of them. The first version of this test did resolve from the
    // host, and every operation quietly found nothing: both "failed", the
    // assertion that would have said so was swallowed by the catch below, and
    // the test failed for a reason that had nothing to do with locking.
    //
    // So: a fixed tenant, and the *real* TenantSessionContextInterceptor wired to
    // the same instance, so mechanisms 1 and 3 both see Acme. Same
    // EnableRetryOnFailure configuration as production, because the execution
    // strategy is part of what is under test — a deadlock victim here is a
    // correct outcome that gets retried, not a failure.
    private async Task<Outcome> RunGuardedWriteAsync(
        Guid userId,
        TimeSpan holdFor,
        Action<User, DateTime> mutate)
    {
        var currentTenant = new FixedCurrentTenant(_acmeOrgId);
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(
                IntegrationTestSettings.ConnectionStringFor(AuthenticationTestHost.DatabaseName),
                sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: [1205]))
            .AddInterceptors(new TenantSessionContextInterceptor(currentTenant))
            .Options;

        await using var context = new BookSpaceDbContext(options, currentTenant);
        var users = new UserRepository(context);
        var unitOfWork = new UnitOfWork(context);

        try
        {
            return await unitOfWork.ExecuteAsync(
                async token =>
                {
                    var user = await users.FindForUpdateAsync(userId, token);
                    if (user is null)
                    {
                        // Returned, never asserted: an assertion in here would be
                        // caught by the handler below and reported as an ordinary
                        // refusal. That is exactly how the first version of this
                        // test hid its own misconfiguration.
                        return new Outcome(false, RefusedByGuard: false, Failure: null, NotFound: true);
                    }

                    var others = await users.CountOtherActiveTenantAdminsAsync(userId, token);

                    if (holdFor > TimeSpan.Zero)
                    {
                        await Task.Delay(holdFor, token);
                    }

                    if (others == 0)
                    {
                        return new Outcome(false, RefusedByGuard: true, Failure: null);
                    }

                    mutate(user, DateTime.UtcNow);
                    await context.SaveChangesAsync(token);

                    return new Outcome(true, RefusedByGuard: false, Failure: null);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new Outcome(false, RefusedByGuard: false, Failure: ex);
        }
    }

    private sealed record Outcome(
        bool Succeeded,
        bool RefusedByGuard,
        Exception? Failure,
        bool NotFound = false);

    // ---- Fixture helpers ----

    private async Task<Guid> AddAdminAsync(BookSpaceDbContext context, Guid createdBy)
    {
        var user = new User(
            Guid.NewGuid(),
            _acmeOrgId,
            $"race-admin-{Guid.NewGuid():N}@acme.test",
            "placeholder-hash",
            "Race Admin",
            createdBy,
            DateTime.UtcNow);

        user.AddRole(Role.TenantAdmin, createdBy, DateTime.UtcNow);
        context.Users.Add(user);
        _createdUserIds.Add(user.Id);

        return user.Id;
    }

    private async Task SetSeededAdminRoleAsync(bool hasRole)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var admin = await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == AcmeAdmin);

        if (hasRole)
        {
            admin.AddRole(Role.TenantAdmin, admin.Id, DateTime.UtcNow);
        }
        else
        {
            admin.RemoveRole(Role.TenantAdmin, admin.Id, DateTime.UtcNow);
        }

        await context.SaveChangesAsync();
    }

    private async Task ReactivateAsync(Guid userId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var user = await context.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        user.Reactivate(user.Id, DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task<int> CountActiveAdminsAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();
        var users = await context.Users
            .IgnoreQueryFilters()
            .Where(u => u.OrgId == _acmeOrgId && u.IsActive)
            .ToListAsync();

        return users.Count(u => u.Roles.Contains(Role.TenantAdmin));
    }
}
