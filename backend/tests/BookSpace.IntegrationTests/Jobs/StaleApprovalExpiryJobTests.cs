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

// WP-8 Phase 4 (docs/wp8-plan.md, FR-9.3, decisions D3/D3a). End to end: real
// Pending ApprovalRequests, past their own ExpiresAtUtc, expired against a
// real SQL Server and rejecting a real Booking. AuthenticationTestHost strips
// the real hosted service (see its own comment) — every run here is driven
// directly via RunOnceAsync, never the ambient timer loop.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class StaleApprovalExpiryJobTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _bookingIds = [];

    private Guid _resourceId;
    private Guid _recipientUserId;
    private Guid _deciderUserId;

    public StaleApprovalExpiryJobTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    public async Task InitializeAsync()
    {
        await ExpireLeaseAsync();

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var member = await context.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.Email == "member1@acme.test");

        // ApprovalRequest.DecidedByUserId is a real FK into Users
        // (FK_ApprovalRequests_Users) — DecideAsync's simulated human
        // decision needs a real row, not an arbitrary Guid.
        var decider = await context.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.Email == "admin@acme.test");

        // Its own dedicated resource, at a capacity nothing this file does
        // can exhaust — the same reasoning NotificationDispatchJobTests and
        // NoShowReleaseJobTests give for not reusing the seeded
        // "Conference Room A".
        var resource = new Resource(
            Guid.NewGuid(), member.OrgId!.Value, "WP-8 Stale-Approval Test Room", ResourceType.Room,
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
        _deciderUserId = decider.Id;
    }

    public async Task DisposeAsync()
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var approvals = await context.ApprovalRequests
            .Where(a => _bookingIds.Contains(a.BookingId)).ToListAsync();
        context.ApprovalRequests.RemoveRange(approvals);
        await context.SaveChangesAsync();

        var notifications = await context.Notifications
            .Where(n => n.BookingId != null && _bookingIds.Contains(n.BookingId.Value)).ToListAsync();
        context.Notifications.RemoveRange(notifications);
        await context.SaveChangesAsync();

        var bookings = await context.Bookings.IgnoreQueryFilters()
            .Where(b => _bookingIds.Contains(b.Id)).ToListAsync();
        context.Bookings.RemoveRange(bookings);
        await context.SaveChangesAsync();

        var resource = await context.Resources.IgnoreQueryFilters()
            .Where(r => r.Id == _resourceId).ToListAsync();
        context.Resources.RemoveRange(resource);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ExpiresAPendingApprovalPastItsExpiryAndRejectsTheBooking()
    {
        var bookingId = await CreatePendingApprovalAsync(expiresOffsetHours: -1);

        var outcome = await RunJobAsync();

        Assert.True(outcome.LeaseAcquired);
        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);

        Assert.Equal(BookingStatus.Rejected, await BookingStatusAsync(bookingId));
        Assert.Null(await BookingUpdatedByUserIdAsync(bookingId));
        Assert.Equal(ApprovalDecision.Expired, await ApprovalDecisionAsync(bookingId));
        Assert.Equal(1, await ApprovalExpiredNotificationCountAsync(bookingId));
    }

    [Fact]
    public async Task LeavesAPendingApprovalStillWithinItsWindowAlone()
    {
        var bookingId = await CreatePendingApprovalAsync(expiresOffsetHours: 1);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(0, 0, 0), outcome.Summary);
        Assert.Equal(BookingStatus.Pending, await BookingStatusAsync(bookingId));
        Assert.Equal(ApprovalDecision.Pending, await ApprovalDecisionAsync(bookingId));
    }

    // Organizations.ApprovalExpiryHours may be null for a tenant that has
    // configured no expiry — a Pending request with no ExpiresAtUtc waits
    // indefinitely, and must never be picked up regardless of how old it is.
    [Fact]
    public async Task LeavesAnApprovalWithNoConfiguredExpiryAlone()
    {
        var bookingId = await CreatePendingApprovalAsync(expiresOffsetHours: null);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(0, 0, 0), outcome.Summary);
        Assert.Equal(BookingStatus.Pending, await BookingStatusAsync(bookingId));
    }

    // Already decided (by a human, in this fixture) before the sweep ever
    // runs — must be left alone regardless of how far in the past
    // ExpiresAtUtc is, since IX_ApprovalRequests_Pending and the job's own
    // predicate both filter on Decision = Pending.
    [Fact]
    public async Task LeavesAnAlreadyDecidedApprovalAlone()
    {
        var bookingId = await CreatePendingApprovalAsync(expiresOffsetHours: -1);
        await DecideAsync(bookingId, ApprovalDecision.Approved);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(0, 0, 0), outcome.Summary);
        Assert.Equal(BookingStatus.Confirmed, await BookingStatusAsync(bookingId));
        Assert.Equal(ApprovalDecision.Approved, await ApprovalDecisionAsync(bookingId));
    }

    // The point of decision D3a: a human's decision landing between the
    // sweep's read and its expiry must win, not be silently overwritten.
    [Fact]
    public async Task ARaceWithAConcurrentDecisionDoesNotOverwriteIt()
    {
        var bookingId = await CreatePendingApprovalAsync(expiresOffsetHours: -1);

        await using var scope = _host.CreateScope();
        var expiries = scope.ServiceProvider.GetRequiredService<IStaleApprovalExpiryRepository>();

        var candidates = await expiries.FindExpiredCandidatesAsync(10, DateTime.UtcNow, CancellationToken.None);
        var candidate = candidates.Single(c => c.Booking.Id == bookingId);

        // A concurrent human decision, landing on its own connection/context
        // — the same shape a real second request would take. A distinct
        // real user (not _recipientUserId, the booking's own owner) so the
        // assertion below can tell the human actor apart from the job's own
        // null-actor transition.
        var deciderId = _deciderUserId;
        await DecideAsync(bookingId, ApprovalDecision.Rejected, deciderId);

        var released = await expiries.TryExpireAsync(candidate, DateTime.UtcNow, CancellationToken.None);

        Assert.False(released);
        Assert.Equal(BookingStatus.Rejected, await BookingStatusAsync(bookingId));
        // The human's own actor survives — not overwritten by the job's
        // null-actor transition.
        Assert.Equal(deciderId, await BookingUpdatedByUserIdAsync(bookingId));
        Assert.Equal(ApprovalDecision.Rejected, await ApprovalDecisionAsync(bookingId));
        Assert.Equal(0, await ApprovalExpiredNotificationCountAsync(bookingId));
    }

    // Once expired, a second run must not find it again — Decision is no
    // longer Pending, so it falls outside both IX_ApprovalRequests_Pending
    // and the job's own predicate.
    [Fact]
    public async Task ASecondRunDoesNotReprocessAnAlreadyExpiredApproval()
    {
        var bookingId = await CreatePendingApprovalAsync(expiresOffsetHours: -1);

        await RunJobAsync();

        // A fresh StaleApprovalExpiryJob instance below is a fresh owner id
        // (PeriodicJobRunner mints one per instance, not per run), so the
        // lease the first call just won has to be expired again first.
        await ExpireLeaseAsync();
        var second = await RunJobAsync();

        Assert.Equal(new JobRunSummary(0, 0, 0), second.Summary);
        Assert.Equal(BookingStatus.Rejected, await BookingStatusAsync(bookingId));
        Assert.Equal(1, await ApprovalExpiredNotificationCountAsync(bookingId));
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
            WHERE JobName = 'StaleApprovalExpiry'
            """);
    }

    private async Task<JobRunOutcome> RunJobAsync()
    {
        var job = new StaleApprovalExpiryJob(
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StaleApprovalExpiryJob>.Instance,
            _host.Services.GetRequiredService<IOptionsMonitor<StaleApprovalExpiryOptions>>());

        return await job.RunOnceAsync(CancellationToken.None);
    }

    // Created via dbo.CreateBooking, requesting Status = Pending directly
    // (decision 0017) — the procedure honours a requested Pending
    // regardless of the resource's own RequiresApproval flag, since neither
    // Pending nor Confirmed differs from the other's capacity claim
    // (decision 0005). The ApprovalRequest row is then added directly,
    // since this fixture needs an ExpiresAtUtc the real create handler would
    // never backdate — the same carve-out NoShowReleaseJobTests uses for a
    // backdated StartsAtUtc.
    private async Task<Guid> CreatePendingApprovalAsync(int? expiresOffsetHours)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();

        using var _ = TenantBypassScope.Enter();

        var start = DateTime.UtcNow.AddDays(7);
        var bookingId = Guid.NewGuid();

        await bookings.CreateAsync(
            new NewBooking(
                bookingId, _resourceId, _recipientUserId, RecurrenceRuleId: null,
                start, start.AddHours(1), Quantity: 1, Title: "Stale-approval test fixture",
                BookingStatus.Pending, CreatedByUserId: _recipientUserId, DateTime.UtcNow),
            CancellationToken.None);

        var requestedAt = DateTime.UtcNow;
        var expiresAt = expiresOffsetHours is { } hours ? requestedAt.AddHours(hours) : (DateTime?)null;

        context.ApprovalRequests.Add(new ApprovalRequest(Guid.NewGuid(), bookingId, requestedAt, expiresAt));
        await context.SaveChangesAsync();

        _bookingIds.Add(bookingId);
        return bookingId;
    }

    // Simulates a human decision landing on this booking/approval pair.
    // Reject goes through the real domain method (Booking.Reject), the same
    // write path RejectBookingCommandRequestHandler uses. Approve has no
    // domain method to call here — a real approval's Status change goes
    // through dbo.ApproveBooking, not a plain EF write — so the tracked
    // property is overridden directly instead, the same technique
    // NotificationDispatchJobTests uses to force a Notification's Kind: this
    // fixture only needs the Booking to *look* Confirmed for
    // LeavesAnAlreadyDecidedApprovalAlone's assertions, not to prove the
    // procedure's own behaviour, which CreateBookingProcedureTests already
    // covers.
    private async Task DecideAsync(Guid bookingId, ApprovalDecision decision, Guid? deciderId = null)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var booking = await context.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        var approval = await context.ApprovalRequests.SingleAsync(a => a.BookingId == bookingId);

        var actor = deciderId ?? _deciderUserId;
        var now = DateTime.UtcNow;

        approval.Decide(decision, actor, now, null);

        switch (decision)
        {
            case ApprovalDecision.Rejected:
                booking.Reject(actor, now);
                break;
            case ApprovalDecision.Approved:
                context.Entry(booking).Property(nameof(Booking.Status)).CurrentValue = BookingStatus.Confirmed;
                context.Entry(booking).Property(nameof(Booking.UpdatedByUserId)).CurrentValue = actor;
                break;
        }

        await context.SaveChangesAsync();
    }

    private async Task<BookingStatus> BookingStatusAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        return await context.Bookings.IgnoreQueryFilters()
            .Where(b => b.Id == bookingId).Select(b => b.Status).SingleAsync();
    }

    private async Task<Guid?> BookingUpdatedByUserIdAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        return await context.Bookings.IgnoreQueryFilters()
            .Where(b => b.Id == bookingId).Select(b => b.UpdatedByUserId).SingleAsync();
    }

    private async Task<ApprovalDecision> ApprovalDecisionAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.ApprovalRequests
            .Where(a => a.BookingId == bookingId).Select(a => a.Decision).SingleAsync();
    }

    private async Task<int> ApprovalExpiredNotificationCountAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.Notifications
            .Where(n => n.BookingId == bookingId
                && n.Kind == NotificationKind.ApprovalExpired
                && n.RecipientUserId == _recipientUserId
                && n.SentAtUtc == null)
            .CountAsync();
    }
}
