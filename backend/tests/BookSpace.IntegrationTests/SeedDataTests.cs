using BookSpace.Application.Abstractions;
using BookSpace.Domain.Enums;
using BookSpace.IntegrationTests.Support;
using BookSpace.Infrastructure.Persistence;
using BookSpace.Infrastructure.Persistence.Repositories;
using BookSpace.Infrastructure.Time;
using BookSpace.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.IntegrationTests;

// Needs a real local SQL Server instance — consistent with CLAUDE.md §8's
// rule that anything with real database behavior (locking, RLS, and here,
// actual constraint enforcement) must use a real SQL Server, not the
// in-memory provider. Runs against its own throwaway database so it never
// touches the "BookSpace" dev database that the app itself seeds.
//
// Every assertion here is a deliberate cross-tenant read (all seeded users,
// all seeded resources, at once) — this file intentionally acts as an
// unfiltered probe over SeedData's raw output, the same justification
// AuthenticationUserRepository has for its own IgnoreQueryFilters() use
// (CLAUDE.md §4.2). It's wired with a fixed no-tenant ICurrentTenant plus the
// RLS interceptor, and every query against Users/Resources/Bookings wraps in
// TenantBypassScope.Enter() and adds IgnoreQueryFilters() — bypassing both
// the EF-level filter and the DB-level RLS layer, which are independent.
public class SeedDataTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        IntegrationTestSettings.ConnectionStringFor("BookSpace_SeedDataTests");

    // Real hasher: the seed now stores genuine PBKDF2 hashes, and asserting on
    // that is part of proving FR-2.3 holds for seeded accounts too.
    private static readonly IPasswordHasher PasswordHasher = new PasswordHasherAdapter();

    // Real catalog too, for the same reason: the seeded bookings are
    // resource-local wall clocks converted with the host's tzdata, and a fake
    // offset would stop this file proving that they land inside the seeded
    // 09:00–17:00 schedule.
    private static readonly ITimeZoneCatalog TimeZones = new SystemTimeZoneCatalog();

    private BookSpaceDbContext _context = null!;

    // The real repository over the hand-built context, so the seeded bookings go
    // through dbo.CreateBooking exactly as they do in Program.cs — CLAUDE.md
    // §4.1 admits no other way to write one, and a stub here would test nothing.
    private IBookingRepository _bookings = null!;

    public async Task InitializeAsync()
    {
        var currentTenant = new FixedCurrentTenant(null);
        var options = new DbContextOptionsBuilder<BookSpaceDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(new TenantSessionContextInterceptor(currentTenant))
            .Options;
        _context = new BookSpaceDbContext(options, currentTenant);
        _bookings = new BookingRepository(_context);

        await _context.Database.EnsureDeletedAsync();
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.EnsureDeletedAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task SeedAsync_PopulatesExpectedRowCounts()
    {
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);

        using var _ = TenantBypassScope.Enter();

        Assert.Equal(2, await _context.Organizations.CountAsync());

        var users = await _context.Users.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(9, users.Count);
        Assert.Equal(9, users.Sum(u => u.Roles.Count)); // owned collections load with the owner

        Assert.Equal(4, await _context.Resources.IgnoreQueryFilters().CountAsync());
        // IgnoreQueryFilters as of WP-3 decision D1: these two tables are now
        // tenant-filtered like Users/Resources/Bookings, and this context has no
        // current tenant. TenantBypassScope covers RLS only, not the EF filter.
        Assert.Equal(20, await _context.AvailabilityWindows.IgnoreQueryFilters().CountAsync());
        Assert.Equal(2, await _context.BlackoutPeriods.IgnoreQueryFilters().CountAsync());
        // Decision 0025: RecurrenceRules joined the tenant-filtered tables in
        // WP-5 Phase 2, same reasoning as the three above.
        Assert.Equal(2, await _context.RecurrenceRules.IgnoreQueryFilters().CountAsync());

        // WP-4 Phase 3: this was 0 from WP-1 until 2026-09-08, because
        // dbo.CreateBooking did not exist and CLAUDE.md §4.1 admits no other way
        // to write a booking. Three per tenant now — two Confirmed on the
        // conference room, one Pending on the printer — each with its own
        // ApprovalRequest where the resource requires approval.
        Assert.Equal(6, await _context.Bookings.IgnoreQueryFilters().CountAsync());
        Assert.Equal(2, await _context.ApprovalRequests.CountAsync());

        // Nothing is enqueued for them, deliberately: no job dispatches
        // Notifications yet (CLAUDE.md §7), so seeded rows would be permanently
        // unsent mail rather than realism.
        Assert.Equal(0, await _context.Notifications.CountAsync());
    }

    // The seeded bookings are the one part of this dataset written *through* the
    // rules rather than around them — dbo.CreateBooking refuses a blacked-out or
    // over-capacity interval, and SeedData throws rather than continuing — so
    // this asserts the shape that survived, and that the intervals actually land
    // inside the seeded 09:00–17:00 local schedule.
    [Fact]
    public async Task SeedAsync_BookingsAreInTheFutureAndInsideTheSeededSchedule()
    {
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);

        using var _ = TenantBypassScope.Enter();

        var bookings = await _context.Bookings.IgnoreQueryFilters().ToListAsync();
        var resources = await _context.Resources.IgnoreQueryFilters().ToListAsync();
        var organizations = await _context.Organizations.ToListAsync();

        Assert.Equal(4, bookings.Count(b => b.Status == BookingStatus.Confirmed));
        Assert.Equal(2, bookings.Count(b => b.Status == BookingStatus.Pending));

        // A Pending booking is on an approval-gated resource and a Confirmed one
        // is not — FR-7.1's routing, produced by the same handler rule the
        // endpoint uses rather than asserted into the data.
        Assert.All(bookings, booking =>
        {
            var resource = resources.Single(r => r.Id == booking.ResourceId);

            Assert.Equal(
                resource.RequiresApproval ? BookingStatus.Pending : BookingStatus.Confirmed,
                booking.Status);
        });

        foreach (var booking in bookings)
        {
            var resource = resources.Single(r => r.Id == booking.ResourceId);
            var org = organizations.Single(o => o.Id == resource.OrgId);
            var zone = TimeZones.GetResourceTimeZone(org.TimeZoneId);

            // In the future, or POST /bookings would refuse the same interval
            // with BookingInThePast.
            Assert.True(booking.StartsAtUtc > DateTime.UtcNow, "A seeded booking is in the past.");

            var localStart = zone.ToLocal(booking.StartsAtUtc);
            var localEnd = zone.ToLocal(booking.EndsAtUtc);

            // Monday–Friday, 09:00–17:00 local: the schedule AddWeekdayWindows
            // gives every seeded resource.
            Assert.InRange(localStart.DayOfWeek, DayOfWeek.Monday, DayOfWeek.Friday);
            Assert.Equal(localStart.Date, localEnd.Date);
            Assert.InRange(localStart.TimeOfDay, TimeSpan.FromHours(9), TimeSpan.FromHours(17));
            Assert.InRange(localEnd.TimeOfDay, TimeSpan.FromHours(9), TimeSpan.FromHours(17));

            // And inside the resource's own duration limits, which nothing else
            // in the seed path checks — dbo.CreateBooking deliberately does not
            // (decision 0023), so a seeded booking that broke them would only
            // surface as a resource nobody could edit.
            Assert.True(
                resource.AllowsBookingDuration(booking.EndsAtUtc - booking.StartsAtUtc),
                $"A seeded booking on '{resource.Name}' is outside its duration limits.");
        }
    }

    // WP-3 decision D1: every seeded child row carries the OrgId of the resource
    // it hangs off. FK_*_Resources_SameOrg enforces this at the database level;
    // this asserts the seed actually produces it, not just that it could.
    [Fact]
    public async Task SeedAsync_ChildRowsCarryTheirResourcesOrgId()
    {
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);

        using var _ = TenantBypassScope.Enter();

        var resourceOrgById = await _context.Resources.IgnoreQueryFilters()
            .ToDictionaryAsync(r => r.Id, r => r.OrgId);

        var windows = await _context.AvailabilityWindows.IgnoreQueryFilters().ToListAsync();
        Assert.NotEmpty(windows);
        Assert.All(windows, w => Assert.Equal(resourceOrgById[w.ResourceId], w.OrgId));

        var blackouts = await _context.BlackoutPeriods.IgnoreQueryFilters().ToListAsync();
        Assert.NotEmpty(blackouts);
        Assert.All(blackouts, b => Assert.Equal(resourceOrgById[b.ResourceId], b.OrgId));
    }

    // FR-2.3: the seed stores real hashes now, not the WP-1 placeholder — so the
    // accounts are actually usable for login and no plaintext is on disk.
    [Fact]
    public async Task SeedAsync_StoresVerifiablePasswordHashesNotPlaintext()
    {
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);

        using var _ = TenantBypassScope.Enter();
        var users = await _context.Users.IgnoreQueryFilters().ToListAsync();

        Assert.All(users, u =>
        {
            Assert.DoesNotContain(SeedData.SeedPassword, u.PasswordHash);
            Assert.True(PasswordHasher.Verify(u.PasswordHash, SeedData.SeedPassword));
        });
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent_WhenCalledTwice()
    {
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);

        using var _ = TenantBypassScope.Enter();

        Assert.Equal(2, await _context.Organizations.CountAsync());
        Assert.Equal(9, await _context.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SeedAsync_BootstrapSysAdmin_SelfReferencesCreatedBy()
    {
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);

        using var _ = TenantBypassScope.Enter();
        var sysAdmin = await _context.Users.IgnoreQueryFilters().SingleAsync(u => u.OrgId == null);

        Assert.Equal(sysAdmin.Id, sysAdmin.CreatedByUserId);
    }

    [Fact]
    public async Task SeedAsync_EachOrganizationHasATenantAdmin()
    {
        await SeedData.SeedAsync(_context, PasswordHasher, _bookings, TimeZones);

        using var _ = TenantBypassScope.Enter();
        var orgIds = await _context.Organizations.Select(o => o.Id).ToListAsync();

        foreach (var orgId in orgIds)
        {
            var usersInOrg = await _context.Users.IgnoreQueryFilters().Where(u => u.OrgId == orgId).ToListAsync();
            Assert.Equal(4, usersInOrg.Count);
        }
    }
}
