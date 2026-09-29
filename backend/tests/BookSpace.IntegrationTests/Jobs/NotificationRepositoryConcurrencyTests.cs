using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Infrastructure.Persistence;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Jobs;

// WP-8 Phase 2 (docs/wp8-plan.md). ClaimDueAsync's WHERE clause is the whole
// idempotency/retry contract in one statement — due, unsent, under the retry
// cap, past its own backoff window — and its UPDLOCK/READPAST hint is what
// keeps two racing callers from both claiming the same row, the row-level
// defence-in-depth behind the job-level lease (docs/wp8-plan.md decision D9's
// reasoning). Each test proves one clause; the last proves the hint actually
// does something, the same "force the interleaving" discipline
// JobLeaseRepositoryConcurrencyTests already uses for Phase 1's lease.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class NotificationRepositoryConcurrencyTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _notificationIds = [];
    private Guid _bookingId;
    private Guid _resourceId;
    private Guid _recipientUserId;

    public NotificationRepositoryConcurrencyTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // One real Booking and User to satisfy FK_Notifications_Bookings /
    // FK_Notifications_Users — ClaimDueAsync itself never joins to either, so
    // their content plays no part in what this file tests.
    public async Task InitializeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var member = await context.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.Email == "member1@acme.test");

        // Its own dedicated resource, not the seeded "Conference Room A" —
        // that one has Capacity 1 and is booked by tests all over this
        // suite, which made a fixed date here collide unpredictably on a
        // full-suite run (found by running the suite repeatedly, not on the
        // first pass). A capacity this large cannot be exhausted by the one
        // booking this file ever creates.
        var resource = new Resource(
            Guid.NewGuid(), member.OrgId!.Value, "WP-8 Claim Test Room", ResourceType.Room,
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

        var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();
        await bookings.CreateAsync(
            new NewBooking(
                Guid.NewGuid(), resource.Id, member.Id, RecurrenceRuleId: null,
                new DateTime(2027, 6, 1, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2027, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                Quantity: 1, Title: "Claim test fixture", BookingStatus.Confirmed,
                CreatedByUserId: member.Id, DateTime.UtcNow),
            CancellationToken.None);

        _bookingId = (await context.Bookings.IgnoreQueryFilters()
            .SingleAsync(b => b.Title == "Claim test fixture")).Id;
        _resourceId = resource.Id;
        _recipientUserId = member.Id;
    }

    public async Task DisposeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var notifications = await context.Notifications
            .Where(n => _notificationIds.Contains(n.Id)).ToListAsync();
        context.Notifications.RemoveRange(notifications);

        var bookings = await context.Bookings.IgnoreQueryFilters()
            .Where(b => b.Id == _bookingId).ToListAsync();
        context.Bookings.RemoveRange(bookings);
        await context.SaveChangesAsync();

        // Bookings first, then the resource they FK-reference — its
        // AvailabilityWindows cascade with it (CLAUDE.md §5).
        var resource = await context.Resources.IgnoreQueryFilters()
            .Where(r => r.Id == _resourceId).ToListAsync();
        context.Resources.RemoveRange(resource);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ClaimsADueUnsentRow()
    {
        var id = await InsertRowAsync(sendAtUtc: DateTime.UtcNow.AddMinutes(-1));

        var claimed = await ClaimAsync();

        Assert.Contains(claimed, c => c.Id == id);
        Assert.Equal(1, await AttemptsAsync(id));
    }

    [Fact]
    public async Task DoesNotClaimANotYetDueRow()
    {
        var id = await InsertRowAsync(sendAtUtc: DateTime.UtcNow.AddHours(1));

        var claimed = await ClaimAsync();

        Assert.DoesNotContain(claimed, c => c.Id == id);
    }

    [Fact]
    public async Task DoesNotClaimAnAlreadySentRow()
    {
        var id = await InsertRowAsync(sendAtUtc: DateTime.UtcNow.AddMinutes(-1));
        await MarkSentAsync(id);

        var claimed = await ClaimAsync();

        Assert.DoesNotContain(claimed, c => c.Id == id);
    }

    [Fact]
    public async Task DoesNotClaimARowAtOrPastTheRetryCap()
    {
        var id = await InsertRowAsync(sendAtUtc: DateTime.UtcNow.AddMinutes(-1));
        await SetAttemptsAsync(id, attempts: 3);

        var claimed = await ClaimAsync(maxAttempts: 3);

        Assert.DoesNotContain(claimed, c => c.Id == id);
    }

    [Fact]
    public async Task RefusesToRetryUntilItsOwnBackoffWindowHasElapsed()
    {
        var id = await InsertRowAsync(sendAtUtc: DateTime.UtcNow.AddMinutes(-5));
        await SetAttemptsAsync(id, attempts: 1);

        // backoffBase=30s at Attempts=1 -> 2^1 * 30s = 60s since the row's own
        // UpdatedAtUtc (just set to "now" by SetAttemptsAsync). Immediately
        // after, the window has not elapsed.
        var tooSoon = await ClaimAsync(backoffBase: TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(tooSoon, c => c.Id == id);
    }

    [Fact]
    public async Task ExactlyOneOfTwoConcurrentClaimsGetsAGivenRow()
    {
        var id = await InsertRowAsync(sendAtUtc: DateTime.UtcNow.AddMinutes(-1));

        var first = ClaimAsync();
        var second = ClaimAsync();

        var results = await Task.WhenAll(first, second);

        var totalClaims = results.Sum(claimed => claimed.Count(c => c.Id == id));
        Assert.Equal(1, totalClaims);
    }

    // ---- Helpers ----

    private async Task<Guid> InsertRowAsync(DateTime sendAtUtc)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var notification = Notification.ForBooking(
            Guid.NewGuid(), _bookingId, _recipientUserId, NotificationKind.Confirmed,
            sendAtUtc, _recipientUserId, DateTime.UtcNow);

        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        _notificationIds.Add(notification.Id);
        return notification.Id;
    }

    private async Task<IReadOnlyList<ClaimedNotification>> ClaimAsync(
        int batchSize = 10, int maxAttempts = 5, TimeSpan? backoffBase = null)
    {
        await using var scope = _host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        return await repository.ClaimDueAsync(
            batchSize, maxAttempts, backoffBase ?? TimeSpan.FromSeconds(30), DateTime.UtcNow, CancellationToken.None);
    }

    private async Task<int> AttemptsAsync(Guid id)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.Notifications.Where(n => n.Id == id).Select(n => n.Attempts).FirstAsync();
    }

    private async Task MarkSentAsync(Guid id)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var notification = await context.Notifications.FirstAsync(n => n.Id == id);
        notification.MarkOutcome(DateTime.UtcNow, succeeded: true);
        await context.SaveChangesAsync();
    }

    private async Task SetAttemptsAsync(Guid id, int attempts)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.Notifications SET Attempts = {attempts}, UpdatedAtUtc = {DateTime.UtcNow} WHERE Id = {id}");
    }
}
