using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Jobs;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BookSpace.IntegrationTests.Jobs;

// WP-8 Phase 3 (docs/wp8-plan.md, FR-9.1, decision 0004). End to end: real
// Confirmed bookings, past a real org's NoShowGraceMinutes (Acme is seeded
// at 15 — SeedData.CreateOrg), released against a real SQL Server and
// queuing a real NoShowReleased row. AuthenticationTestHost strips the real
// hosted service (see its own comment) — every run here is driven directly
// via RunOnceAsync, never the ambient timer loop.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class NoShowReleaseJobTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _bookingIds = [];

    private Guid _resourceId;
    private Guid _recipientUserId;

    public NoShowReleaseJobTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    public async Task InitializeAsync()
    {
        // The real seeded dbo.JobLeases row, shared by every test in this
        // file — reset to an expired lease before every test so each one's
        // own RunOnceAsync call (a fresh owner id every time) always finds
        // it acquirable, the same reasoning NotificationDispatchJobTests
        // gives for its own reset.
        await ExpireLeaseAsync();

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var member = await context.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.Email == "member1@acme.test");

        // Its own dedicated resource, at a capacity nothing this file does
        // can exhaust — the same reasoning NotificationDispatchJobTests
        // gives for not reusing the seeded "Conference Room A".
        var resource = new Resource(
            Guid.NewGuid(), member.OrgId!.Value, "WP-8 No-Show Test Room", ResourceType.Room,
            capacity: 1000, timeZoneId: "UTC", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: member.Id, DateTime.UtcNow);
        resource.ReplaceAvailabilityWindows(
            Enum.GetValues<DayOfWeek>().Select(day => new AvailabilityWindowDefinition(
                Guid.NewGuid(), day, new TimeOnly(0, 0), new TimeOnly(23, 59, 59))),
            member.Id,
            DateTime.UtcNow);

        context.Resources.Add(resource);
        await context.SaveChangesAsync();

        _resourceId = resource.Id;
        _recipientUserId = member.Id;
    }

    public async Task DisposeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var bookings = await context.Bookings.IgnoreQueryFilters()
            .Where(b => _bookingIds.Contains(b.Id)).ToListAsync();
        context.Bookings.RemoveRange(bookings);
        await context.SaveChangesAsync();

        var notifications = await context.Notifications
            .Where(n => n.BookingId != null && _bookingIds.Contains(n.BookingId.Value)).ToListAsync();
        context.Notifications.RemoveRange(notifications);
        await context.SaveChangesAsync();

        var resource = await context.Resources.IgnoreQueryFilters()
            .Where(r => r.Id == _resourceId).ToListAsync();
        context.Resources.RemoveRange(resource);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ReleasesAConfirmedBookingPastItsOwnOrgsGracePeriod()
    {
        // Acme's grace is 15 minutes (SeedData.CreateOrg); 30 minutes past
        // start is comfortably past it.
        var bookingId = await CreateBookingAsync(startOffsetMinutes: -30);

        var outcome = await RunJobAsync();

        Assert.True(outcome.LeaseAcquired);
        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.Equal(BookingStatus.NoShow, await StatusAsync(bookingId));

        Assert.Equal(1, await NoShowReleasedNotificationCountAsync(bookingId));
    }

    [Fact]
    public async Task LeavesABookingStillWithinItsGracePeriodAlone()
    {
        // 5 minutes past start, well inside Acme's 15-minute grace.
        var bookingId = await CreateBookingAsync(startOffsetMinutes: -5);

        var outcome = await RunJobAsync();

        Assert.True(outcome.LeaseAcquired);
        Assert.Equal(new JobRunSummary(0, 0, 0), outcome.Summary);
        Assert.Equal(BookingStatus.Confirmed, await StatusAsync(bookingId));
    }

    [Fact]
    public async Task LeavesACheckedInBookingAlone()
    {
        var bookingId = await CreateBookingAsync(startOffsetMinutes: -30);
        await CheckInAsync(bookingId);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(0, 0, 0), outcome.Summary);
        Assert.Equal(BookingStatus.Confirmed, await StatusAsync(bookingId));
    }

    // The point of decision D5: a check-in that lands between the sweep's
    // read and its release must win, not be silently overwritten.
    [Fact]
    public async Task ARaceWithAConcurrentCheckInDoesNotOverwriteIt()
    {
        var bookingId = await CreateBookingAsync(startOffsetMinutes: -30);

        await using var scope = _host.CreateScope();
        var noShows = scope.ServiceProvider.GetRequiredService<INoShowReleaseRepository>();

        var candidates = await noShows.FindNoShowCandidatesAsync(10, DateTime.UtcNow, CancellationToken.None);
        var candidate = candidates.Single(b => b.Id == bookingId);

        // A concurrent check-in, landing on its own connection/context —
        // the same shape a real second request would take.
        await using (var otherScope = _host.CreateScope())
        {
            var otherContext = otherScope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
            using var _ = TenantBypassScope.Enter();

            var otherBooking = await otherContext.Bookings.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == bookingId);
            otherBooking.CheckIn(DateTime.UtcNow);
            await otherContext.SaveChangesAsync();
        }

        var released = await noShows.TryReleaseAsync(candidate, DateTime.UtcNow, CancellationToken.None);

        Assert.False(released);
        Assert.Equal(BookingStatus.Confirmed, await StatusAsync(bookingId));
        Assert.Equal(0, await NoShowReleasedNotificationCountAsync(bookingId));
    }

    // Once released, a second run must not find it again — Status is no
    // longer Confirmed, so it falls outside both IX_Bookings_NoShowSweep and
    // the job's own predicate.
    [Fact]
    public async Task ASecondRunDoesNotReprocessAnAlreadyReleasedBooking()
    {
        var bookingId = await CreateBookingAsync(startOffsetMinutes: -30);

        await RunJobAsync();

        // A fresh NoShowReleaseJob instance below is a fresh owner id
        // (PeriodicJobRunner mints one per instance, not per run), so the
        // lease the first call just won has to be expired again first — the
        // same reset InitializeAsync does before every test, needed again
        // here because this test alone calls RunJobAsync twice.
        await ExpireLeaseAsync();
        var second = await RunJobAsync();

        Assert.Equal(new JobRunSummary(0, 0, 0), second.Summary);
        Assert.Equal(BookingStatus.NoShow, await StatusAsync(bookingId));
        Assert.Equal(1, await NoShowReleasedNotificationCountAsync(bookingId));
    }

    // ---- Helpers ----

    private async Task ExpireLeaseAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE dbo.JobLeases
            SET LeaseExpiresAtUtc = '2000-01-01'
            WHERE JobName = 'NoShowRelease'
            """);
    }

    private async Task<JobRunOutcome> RunJobAsync()
    {
        var job = new NoShowReleaseJob(
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NoShowReleaseJob>.Instance,
            _host.Services.GetRequiredService<IOptionsMonitor<NoShowReleaseOptions>>());

        return await job.RunOnceAsync(CancellationToken.None);
    }

    // Created via dbo.CreateBooking (decision 0017), with a past StartsAtUtc
    // the endpoint itself would refuse (BookingInThePast is an app-layer
    // pre-check, not enforced by the procedure) — this fixture bypasses the
    // handler entirely and calls the repository straight, which is exactly
    // what a booking needs to already be old enough to sweep.
    private async Task<Guid> CreateBookingAsync(int startOffsetMinutes)
    {
        await using var scope = _host.CreateScope();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();

        using var _ = TenantBypassScope.Enter();

        var start = DateTime.UtcNow.AddMinutes(startOffsetMinutes);
        var bookingId = Guid.NewGuid();

        await bookings.CreateAsync(
            new NewBooking(
                bookingId, _resourceId, _recipientUserId, RecurrenceRuleId: null,
                start, start.AddHours(1), Quantity: 1, Title: "No-show test fixture",
                BookingStatus.Confirmed, CreatedByUserId: _recipientUserId, DateTime.UtcNow),
            CancellationToken.None);

        _bookingIds.Add(bookingId);
        return bookingId;
    }

    private async Task CheckInAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var booking = await context.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        booking.CheckIn(DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    private async Task<BookingStatus> StatusAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        return await context.Bookings.IgnoreQueryFilters()
            .Where(b => b.Id == bookingId).Select(b => b.Status).SingleAsync();
    }

    private async Task<int> NoShowReleasedNotificationCountAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.Notifications
            .Where(n => n.BookingId == bookingId
                && n.Kind == NotificationKind.NoShowReleased
                && n.RecipientUserId == _recipientUserId
                && n.SentAtUtc == null)
            .CountAsync();
    }
}
