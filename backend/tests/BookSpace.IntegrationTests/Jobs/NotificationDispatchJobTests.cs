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

// WP-8 Phase 2 (docs/wp8-plan.md). End to end: a real due Notification row,
// claimed and composed against real Bookings/RecurrenceRules/Resources/Users
// across TenantBypassScope, sent through the real (DevelopmentSink)
// IEmailSender, and recorded. AuthenticationTestHost strips the real hosted
// service (see its own comment) — every job here is driven directly via
// RunOnceAsync, never the ambient timer loop.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class NotificationDispatchJobTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host;
    private readonly List<Guid> _notificationIds = [];
    private readonly List<Guid> _bookingIds = [];

    // A fixed, far-future date for every booking this file creates — no
    // per-slot offsetting needed, since _resourceId (below) is a resource
    // this file owns outright, at a capacity nothing here can exhaust.
    private static readonly DateTime BookingStartUtc = new(2029, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private Guid _resourceId;
    private Guid _recipientUserId;
    private Guid _recurrenceRuleId;
    private HashSet<string> _preExistingEmailFiles = [];

    public NotificationDispatchJobTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    public async Task InitializeAsync()
    {
        // These tests send real emails through the real (DevelopmentSink)
        // IEmailSender into the shared sink directory —
        // CreateUserEndpointTests' own TryFindSentMessageToAsync searches
        // that same directory by recipient address, and member1@acme.test is
        // exactly who these tests send to. Snapshotting what already exists,
        // rather than clearing the directory outright, is what lets this run
        // in the same collection as tests that sent something earlier and
        // still expect to find it.
        Directory.CreateDirectory(AuthenticationTestHost.EmailSinkDirectory);
        _preExistingEmailFiles = Directory.GetFiles(AuthenticationTestHost.EmailSinkDirectory).ToHashSet();

        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        // The real seeded dbo.JobLeases row (AddJobLeases/
        // RenameReminderDispatchJobLease) is shared by every test in this
        // file, and NotificationDispatchJob.JobName is fixed — unlike Phase
        // 1's lease tests, there is no per-test throwaway job name to use
        // instead, since the real class under test always claims the real
        // name. Reset to an expired lease before every test so each one's own
        // RunOnceAsync call — a fresh owner id every time (a new
        // NotificationDispatchJob instance) — always finds it acquirable,
        // regardless of what the previous test's still-live lease would
        // otherwise say.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE dbo.JobLeases
            SET LeaseExpiresAtUtc = '2000-01-01'
            WHERE JobName = 'NotificationDispatch'
            """);

        using var _ = TenantBypassScope.Enter();

        var member = await context.Users.IgnoreQueryFilters()
            .FirstAsync(u => u.Email == "member1@acme.test");

        // Its own dedicated resource, not the seeded "Conference Room A" —
        // found flaky on repeated full-suite runs, because that resource's
        // Capacity 1 makes it collide with whatever other test in the whole
        // 680-odd-test suite also books it, however far out the date. A
        // capacity this large cannot be exhausted by anything this file
        // does, so no fixed date needs defending against anyone else either.
        var resource = new Resource(
            Guid.NewGuid(), member.OrgId!.Value, "WP-8 Dispatch Test Room", ResourceType.Room,
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

        // The seeded weekly-standup rule on "Conference Room A" — read-only
        // reference for the two RecurrenceRule-anchored kinds below, which
        // never create a Booking and so carry none of that resource's
        // capacity risk.
        var rule = await context.RecurrenceRules.IgnoreQueryFilters()
            .FirstAsync(r => r.UserId == member.Id);

        _resourceId = resource.Id;
        _recipientUserId = member.Id;
        _recurrenceRuleId = rule.Id;
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
            .Where(b => _bookingIds.Contains(b.Id)).ToListAsync();
        context.Bookings.RemoveRange(bookings);
        await context.SaveChangesAsync();

        // Bookings first, then the resource they FK-reference — its
        // AvailabilityWindows cascade with it (CLAUDE.md §5).
        var resource = await context.Resources.IgnoreQueryFilters()
            .Where(r => r.Id == _resourceId).ToListAsync();
        context.Resources.RemoveRange(resource);
        await context.SaveChangesAsync();

        foreach (var file in Directory.GetFiles(AuthenticationTestHost.EmailSinkDirectory))
        {
            if (!_preExistingEmailFiles.Contains(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public async Task DispatchesADueConfirmedNotification()
    {
        var bookingId = await CreateBookingAsync(BookingStatus.Confirmed);
        var notificationId = await InsertBookingNotificationAsync(bookingId, NotificationKind.Confirmed);

        var outcome = await RunJobAsync();

        Assert.True(outcome.LeaseAcquired);
        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.NotNull(await SentAtUtcAsync(notificationId));
    }

    // Decision D10 (docs/wp8-plan.md): a Reminder whose Booking is no longer
    // Confirmed — cancelled here before it fired — has nothing left to remind
    // about. Marked handled (SentAtUtc set) rather than sent or retried.
    [Fact]
    public async Task AReminderForACancelledBookingIsHandledWithoutSendingOrRetrying()
    {
        var bookingId = await CreateBookingAsync(BookingStatus.Confirmed);
        await CancelBookingAsync(bookingId);
        var notificationId = await InsertBookingNotificationAsync(bookingId, NotificationKind.Reminder);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.NotNull(await SentAtUtcAsync(notificationId));
    }

    // ---- Stale notification suppression (hardening pass, finding 5) -------
    //
    // A claimed row can sit unsent for a while (the batch is bounded, the
    // backlog is not), and the booking it describes can move on in the
    // meantime — sometimes producing a genuinely contradictory pair, e.g. a
    // Confirmed row queued and then the booking cancelled before either
    // notification is sent. Suppressing at compose time, keyed off the
    // booking's *current* state, is what actually closes that gap —
    // ClaimDueAsync's own ordering fix (finding 6) makes claiming fairer, but
    // promises nothing about which of two due rows for the same booking goes
    // out first.

    [Fact]
    public async Task AConfirmedNotificationForAnAlreadyCancelledBookingIsHandledWithoutSending()
    {
        var bookingId = await CreateBookingAsync(BookingStatus.Confirmed);
        await CancelBookingAsync(bookingId);
        var notificationId = await InsertBookingNotificationAsync(bookingId, NotificationKind.Confirmed);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.NotNull(await SentAtUtcAsync(notificationId));
    }

    // "Already decided" simulated the same way StaleApprovalExpiryJobTests
    // does — dbo.ApproveBooking is the real write path for this transition,
    // not a plain EF one, so the tracked property is overridden directly
    // rather than re-proving the procedure's own behaviour here.
    [Fact]
    public async Task AnApprovalRequestedNotificationForAnAlreadyApprovedBookingIsHandledWithoutSending()
    {
        var bookingId = await CreateBookingAsync(BookingStatus.Pending);
        await ForceStatusAsync(bookingId, BookingStatus.Confirmed);
        var notificationId = await InsertBookingNotificationAsync(bookingId, NotificationKind.ApprovalRequested);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.NotNull(await SentAtUtcAsync(notificationId));
    }

    // A Reminder arriving after the meeting has already ended is wrong under
    // any reading, with no invented policy needed — deliberately narrower
    // than "already started" (a reminder for a meeting in progress is still
    // arguably useful, and drawing that line would be inventing a rule no FR
    // asks for). Needs its own backdated booking: the file's shared fixture
    // is deliberately far in the future (line 30's own comment).
    [Fact]
    public async Task AReminderForABookingWhoseIntervalHasAlreadyEndedIsHandledWithoutSending()
    {
        var bookingId = await CreateBackdatedConfirmedBookingAsync();
        var notificationId = await InsertBookingNotificationAsync(bookingId, NotificationKind.Reminder);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.NotNull(await SentAtUtcAsync(notificationId));
    }

    // Proves the "one arm per Kind, default throws" composer for every
    // Booking-anchored kind this dispatch job can see today (ApprovalExpired,
    // WP-8 Phase 4, is not built yet) — none of them should throw, and every
    // one should end up sent.
    [Theory]
    [InlineData(NotificationKind.Confirmed, BookingStatus.Confirmed)]
    [InlineData(NotificationKind.Rejected, BookingStatus.Rejected)]
    [InlineData(NotificationKind.Cancelled, BookingStatus.Cancelled)]
    [InlineData(NotificationKind.ApprovalRequested, BookingStatus.Pending)]
    [InlineData(NotificationKind.NoShowReleased, BookingStatus.NoShow)]
    public async Task EveryCurrentBookingAnchoredKindComposesAndSendsWithoutThrowing(
        NotificationKind kind, BookingStatus bookingStatus)
    {
        var bookingId = await CreateBookingAsync(bookingStatus);
        var notificationId = await InsertBookingNotificationAsync(bookingId, kind);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.NotNull(await SentAtUtcAsync(notificationId));
    }

    [Theory]
    [InlineData(NotificationKind.RecurrenceOccurrenceSkipped)]
    [InlineData(NotificationKind.SeriesCancelled)]
    public async Task EveryCurrentRecurrenceRuleAnchoredKindComposesAndSendsWithoutThrowing(NotificationKind kind)
    {
        var notificationId = await InsertRecurrenceNotificationAsync(kind);

        var outcome = await RunJobAsync();

        Assert.Equal(new JobRunSummary(1, 1, 0), outcome.Summary);
        Assert.NotNull(await SentAtUtcAsync(notificationId));
    }

    // "Isolate failure per item" (WP-8's own wording): a Kind anchored to the
    // wrong thing — ApprovalRequested (Booking-anchored) with no BookingId,
    // only a RecurrenceRuleId — is a real, if contrived, way to make the
    // composer's own defensive switch throw for exactly one claimed row,
    // without any fake or decorator standing in for real code. The row beside
    // it must still be sent.
    [Fact]
    public async Task OneFailingRowDoesNotAbortTheRestOfTheBatch()
    {
        var goodBookingId = await CreateBookingAsync(BookingStatus.Confirmed);
        var goodId = await InsertBookingNotificationAsync(goodBookingId, NotificationKind.Confirmed);
        var badId = await InsertRecurrenceNotificationAsync(NotificationKind.ApprovalRequested);

        var outcome = await RunJobAsync();

        Assert.True(outcome.LeaseAcquired);
        Assert.Equal(2, outcome.Summary!.PickedUp);
        Assert.Equal(1, outcome.Summary.Succeeded);
        Assert.Equal(1, outcome.Summary.Failed);

        Assert.NotNull(await SentAtUtcAsync(goodId));
        Assert.Null(await SentAtUtcAsync(badId));
        Assert.NotNull(await LastErrorAsync(badId));
    }

    // ---- Helpers ----

    private async Task<JobRunOutcome> RunJobAsync()
    {
        var job = new NotificationDispatchJob(
            _host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NotificationDispatchJob>.Instance,
            _host.Services.GetRequiredService<IOptionsMonitor<NotificationDispatchOptions>>());

        return await job.RunOnceAsync(CancellationToken.None);
    }

    private async Task<Guid> CreateBookingAsync(BookingStatus status)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();

        using var _ = TenantBypassScope.Enter();

        // Every status this file needs is created Confirmed and then, for the
        // ones dbo.CreateBooking itself never produces (Rejected, Cancelled,
        // NoShow), moved there directly through the domain's own transition —
        // exactly decision 0017's narrowed carve-out: dbo.CreateBooking is
        // still the only way a row is *inserted*, only its post-creation
        // status is adjusted here for a fixture that needs a state nothing
        // ever books into.
        // Reject() requires CanBeRejected() (Status == Pending), so a booking
        // headed for Rejected has to be *created* Pending, same as one headed
        // for Pending itself — Cancel/MarkNoShow, by contrast, both start
        // from Confirmed.
        var initialStatus = status is BookingStatus.Pending or BookingStatus.Rejected
            ? BookingStatus.Pending
            : BookingStatus.Confirmed;

        var outcome = await bookings.CreateAsync(
            new NewBooking(
                Guid.NewGuid(), _resourceId, _recipientUserId, RecurrenceRuleId: null,
                BookingStartUtc, BookingStartUtc.AddHours(1), Quantity: 1, Title: "Dispatch test fixture",
                initialStatus,
                CreatedByUserId: _recipientUserId, DateTime.UtcNow),
            CancellationToken.None);

        var booking = await context.Bookings.IgnoreQueryFilters()
            .SingleAsync(b => b.Title == "Dispatch test fixture" && !_bookingIds.Contains(b.Id));

        switch (status)
        {
            case BookingStatus.Rejected:
                booking.Reject(_recipientUserId, DateTime.UtcNow);
                break;
            case BookingStatus.Cancelled:
                booking.Cancel(_recipientUserId, "Test cleanup", DateTime.UtcNow);
                break;
            case BookingStatus.NoShow:
                booking.MarkNoShow(DateTime.UtcNow);
                break;
        }

        if (status is BookingStatus.Rejected or BookingStatus.Cancelled or BookingStatus.NoShow)
        {
            await context.SaveChangesAsync();
        }

        _bookingIds.Add(booking.Id);
        return booking.Id;
    }

    private async Task CancelBookingAsync(Guid bookingId)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var booking = await context.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        booking.Cancel(_recipientUserId, "Test cancellation", DateTime.UtcNow);
        await context.SaveChangesAsync();
    }

    // Hardening pass, finding 5's own test fixture: dbo.ApproveBooking is the
    // real write path to Confirmed from Pending, not a plain EF one — the
    // tracked property is overridden directly instead, the same technique
    // StaleApprovalExpiryJobTests uses for the identical simulated fact.
    private async Task ForceStatusAsync(Guid bookingId, BookingStatus status)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        using var _ = TenantBypassScope.Enter();

        var booking = await context.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId);
        context.Entry(booking).Property(nameof(Booking.Status)).CurrentValue = status;
        await context.SaveChangesAsync();
    }

    // Created via dbo.CreateBooking with a past interval the endpoint itself
    // would refuse (BookingInThePast is an app-layer pre-check, not enforced
    // by the procedure) — this fixture bypasses the handler entirely and
    // calls the repository straight, the same carve-out
    // NoShowReleaseJobTests uses for its own backdated StartsAtUtc.
    private async Task<Guid> CreateBackdatedConfirmedBookingAsync()
    {
        await using var scope = _host.CreateScope();
        var bookings = scope.ServiceProvider.GetRequiredService<IBookingRepository>();

        using var _ = TenantBypassScope.Enter();

        var start = DateTime.UtcNow.AddHours(-3);
        var bookingId = Guid.NewGuid();

        await bookings.CreateAsync(
            new NewBooking(
                bookingId, _resourceId, _recipientUserId, RecurrenceRuleId: null,
                start, start.AddHours(1), Quantity: 1, Title: "Ended booking test fixture",
                BookingStatus.Confirmed, CreatedByUserId: _recipientUserId, DateTime.UtcNow),
            CancellationToken.None);

        _bookingIds.Add(bookingId);
        return bookingId;
    }

    private async Task<Guid> InsertBookingNotificationAsync(Guid bookingId, NotificationKind kind)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var notification = Notification.ForBooking(
            Guid.NewGuid(), bookingId, _recipientUserId, kind, DateTime.UtcNow.AddMinutes(-1),
            _recipientUserId, DateTime.UtcNow);

        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        _notificationIds.Add(notification.Id);
        return notification.Id;
    }

    // Builds a RecurrenceRule-anchored row for any Kind, including one this
    // dispatch job's composer does not actually support for that anchor
    // (OneFailingRowDoesNotAbortTheRestOfTheBatch's ApprovalRequested case) —
    // CK_Notifications_HasContext only requires *an* anchor, not the "right"
    // one for the Kind, so this row is legal to insert and wrong to compose.
    private async Task<Guid> InsertRecurrenceNotificationAsync(NotificationKind kind)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        var notification = kind == NotificationKind.RecurrenceOccurrenceSkipped
            ? Notification.ForSkippedOccurrence(
                Guid.NewGuid(), _recurrenceRuleId, new DateOnly(2027, 3, 14), _recipientUserId,
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow)
            : Notification.ForSeriesCancelled(
                Guid.NewGuid(), _recurrenceRuleId, _recipientUserId,
                DateTime.UtcNow.AddMinutes(-1), _recipientUserId, DateTime.UtcNow);

        // ForSeriesCancelled always stamps Kind = SeriesCancelled. When the
        // test wants a different Kind on the same RecurrenceRule-only anchor
        // — ApprovalRequested, which the composer only ever expects on a
        // Booking — the tracked property is overridden directly rather than
        // adding a factory method no production code would ever call.
        if (notification.Kind != kind)
        {
            context.Entry(notification).Property(nameof(Notification.Kind)).CurrentValue = kind;
        }

        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        _notificationIds.Add(notification.Id);
        return notification.Id;
    }

    private async Task<DateTime?> SentAtUtcAsync(Guid id)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.Notifications.Where(n => n.Id == id).Select(n => n.SentAtUtc).FirstAsync();
    }

    private async Task<string?> LastErrorAsync(Guid id)
    {
        await using var scope = _host.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BookSpaceDbContext>();

        return await context.Notifications.Where(n => n.Id == id).Select(n => n.LastError).FirstAsync();
    }
}
