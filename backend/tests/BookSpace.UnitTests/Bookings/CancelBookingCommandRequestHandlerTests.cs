using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Bookings.CancelBooking;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Bookings;

// WP-4 Phase 2b. What the cancel handler decides: whose booking it may touch,
// whether this one can still be cancelled, and — the asymmetry worth testing —
// who gets a Cancelled notification row.
//
// The actual filtering is a WHERE clause and is proved against a real SQL Server
// in BookingCancelEndpointTests; here the repository is a fake that hands back
// whichever booking the test wants, which is what makes every branch reachable.
public class CancelBookingCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2027, 3, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2027, 3, 11, 10, 0, 0, DateTimeKind.Utc);

    private static Booking Booking(BookingStatus status = BookingStatus.Confirmed) =>
        new(
            Guid.NewGuid(), OrgId, ResourceId, Owner, null,
            Starts, Ends, 2, "Design review", status, Owner, NowUtc);

    private static CancelBookingCommandRequestHandler Handler(
        FakeBookingRepository bookings,
        Guid actorUserId,
        params Role[] roles) =>
        new(bookings, new FixedCurrentUser(actorUserId, roles), new TestClock(NowUtc));

    // ---- The happy path ----------------------------------------------------

    [Fact]
    public async Task TheOwnerCancelsTheirOwnBooking()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        var response = await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id, "No longer needed"), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(Owner, booking.CancelledByUserId);
        Assert.Equal(NowUtc, booking.CancelledAtUtc);
        Assert.Equal("No longer needed", booking.CancellationReason);

        Assert.Equal(BookingStatus.Cancelled, response.Status);
        Assert.Equal(Owner, response.CancelledByUserId);
        Assert.Equal(1, bookings.SaveChangesCount);
    }

    // The freed interval is on the wire, because the slot it released is the
    // point of the reply — a client updating a calendar needs to know which span
    // just became bookable again.
    [Fact]
    public async Task TheResponseReportsTheFreedInterval()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        var response = await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(ResourceId, response.ResourceId);
        Assert.Equal(Owner, response.UserId);
        Assert.Equal(Starts, response.StartsAtUtc);
        Assert.Equal(Ends, response.EndsAtUtc);
        Assert.Equal(2, response.Quantity);
        Assert.Equal("Design review", response.Title);
    }

    // A reason is optional — "the meeting is off" is often all there is to say.
    [Fact]
    public async Task AReasonIsOptional()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        var response = await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Null(response.CancellationReason);
        Assert.Null(booking.CancellationReason);
    }

    [Fact]
    public async Task APendingBookingCanBeCancelled()
    {
        var booking = Booking(BookingStatus.Pending);
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }

    // ---- Who may cancel (decision 0002) ------------------------------------

    // A member's request carries their own id as the owner filter, so the
    // repository can only ever hand back their own booking.
    [Fact]
    public async Task AMemberMayOnlyReachTheirOwnBooking()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(Owner, bookings.CancellationOwner!.UserId);
    }

    // Decision 0002: a TenantAdmin may cancel any booking in their tenant, so
    // the filter is dropped entirely.
    [Fact]
    public async Task AnAdminMayReachAnyBookingInTheirTenant()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Null(bookings.CancellationOwner!.UserId);
    }

    // An Approver is a non-admin here, as everywhere else: the Approver policy
    // sits between Member and TenantAdmin, so "only an admin" has to mean every
    // non-admin.
    [Fact]
    public async Task AnApproverMayOnlyReachTheirOwnBooking()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Admin, Role.Approver, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(Admin, bookings.CancellationOwner!.UserId);
    }

    // Decision 0002's whole point: the actor is recorded distinctly from the
    // owner, so an admin cancelling someone else's meeting is visible on the row
    // afterwards rather than looking like the owner did it.
    [Fact]
    public async Task AnAdminCancellationRecordsTheAdminAsTheActorAndKeepsTheOwner()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        var response = await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new CancelBookingCommandRequest(booking.Id, "Room repurposed"), CancellationToken.None);

        Assert.Equal(Admin, booking.CancelledByUserId);
        Assert.Equal(Owner, booking.UserId);
        Assert.Equal(Admin, response.CancelledByUserId);
        Assert.Equal(Owner, response.UserId);
    }

    // ---- The notification asymmetry (owner's call, 2026-09-08) -------------

    // Decision 0002's requirement is that the *affected user* is told, and
    // emailing someone the news they just made is noise — the 200 body has
    // already told them.
    [Fact]
    public async Task SelfCancellationEnqueuesNoNotification()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Empty(bookings.AddedNotifications);
    }

    // A cancellation by anyone else does enqueue one, and it goes to the
    // **owner**, not the actor — the actor already knows.
    [Fact]
    public async Task AnAdminCancellationEnqueuesOneNotificationForTheOwner()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        var notification = Assert.Single(bookings.AddedNotifications);
        Assert.Equal(booking.Id, notification.BookingId);
        Assert.Equal(Owner, notification.RecipientUserId);
        Assert.Equal(NotificationKind.Cancelled, notification.Kind);

        // SendAtUtc = now: a cancellation is news, not a reminder, so it is due
        // as soon as the dispatch job (CLAUDE.md §7) next runs.
        Assert.Equal(NowUtc, notification.SendAtUtc);
        Assert.Null(notification.SentAtUtc);

        // The actor is recorded as the creator, unlike the blackout cascade's
        // rows — there a rule caused it, here a person did.
        Assert.Equal(Admin, notification.CreatedByUserId);
    }

    // The suppression keys off the *actor versus the owner*, not off the role.
    // An admin cancelling their own booking gets no notification either, which
    // is the case a role-based check would have got wrong.
    [Fact]
    public async Task AnAdminCancellingTheirOwnBookingEnqueuesNoNotification()
    {
        var booking = new Booking(
            Guid.NewGuid(), OrgId, ResourceId, Admin, null,
            Starts, Ends, 1, "Admin's own", BookingStatus.Confirmed, Admin, NowUtc);
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Empty(bookings.AddedNotifications);
    }

    // ---- The refusals ------------------------------------------------------

    // Null from the repository covers all three cases at once — no such id,
    // another tenant's, another member's — so they cannot produce different
    // answers (AC-4). Never a 403, which would confirm the booking exists.
    [Fact]
    public async Task AnInvisibleBookingIsBookingNotFound()
    {
        var bookings = new FakeBookingRepository { Cancellable = null };

        var exception = await Assert.ThrowsAsync<BookingNotFoundException>(
            () => Handler(bookings, Owner, Role.Member).Handle(
                new CancelBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorKind.NotFound, exception.Kind);
        Assert.Equal(ReasonCodes.BookingNotFound, exception.ReasonCode);
        Assert.Equal(0, bookings.SaveChangesCount);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public async Task ATerminalBookingIsBookingNotCancellable(BookingStatus status)
    {
        var booking = Booking(status);
        var bookings = new FakeBookingRepository { Cancellable = booking };

        var exception = await Assert.ThrowsAsync<BookingNotCancellableException>(
            () => Handler(bookings, Owner, Role.Member).Handle(
                new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(ReasonCodes.BookingNotCancellable, exception.ReasonCode);
    }

    // The second half of BookingNotCancellable, and the half nothing enforced
    // before Phase 2b.
    //
    // Built directly rather than through the Booking helper, whose Starts is in
    // the future: an *ended* booking has to be wholly in the past, and passing
    // only a past end would make EndsAtUtc precede StartsAtUtc, which the
    // constructor refuses (CK_Bookings_Interval) before the handler ever runs.
    [Fact]
    public async Task AnEndedBookingIsBookingNotCancellable()
    {
        var booking = new Booking(
            Guid.NewGuid(), OrgId, ResourceId, Owner, null,
            NowUtc.AddHours(-2), NowUtc.AddHours(-1), 1, "Last week's sync",
            BookingStatus.Confirmed, Owner, NowUtc.AddDays(-1));
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Assert.ThrowsAsync<BookingNotCancellableException>(
            () => Handler(bookings, Owner, Role.Member).Handle(
                new CancelBookingCommandRequest(booking.Id), CancellationToken.None));
    }

    // Nothing is saved on a refusal — asserted rather than assumed, so a handler
    // that threw after mutating would fail here.
    [Fact]
    public async Task NothingIsSavedWhenTheBookingCannotBeCancelled()
    {
        var booking = Booking(BookingStatus.Cancelled);
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Assert.ThrowsAsync<BookingNotCancellableException>(
            () => Handler(bookings, Owner, Role.Member).Handle(
                new CancelBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(0, bookings.SaveChangesCount);
        Assert.Empty(bookings.AddedNotifications);
    }

    // ---- Wiring ------------------------------------------------------------

    // A 500, deliberately: the endpoint sits behind TenantMember, so a request
    // that reached it has a principal. Here it would also mean writing a null
    // into CancelledByUserId, which decision 0002 reserves for the blackout
    // cascade's actor-less transition.
    [Fact]
    public async Task ItRefusesToRunWithoutAnAuthenticatedUser()
    {
        var handler = new CancelBookingCommandRequestHandler(
            new FakeBookingRepository { Cancellable = Booking() },
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new CancelBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));
    }

    // One save covers the status change and the notification row together: a
    // notification whose booking was never cancelled, or a cancellation nobody
    // is told about, would each be unrepairable.
    [Fact]
    public async Task TheStatusChangeAndTheNotificationShareOneSave()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(1, bookings.SaveChangesCount);
        Assert.Single(bookings.AddedNotifications);
    }

    // ---- Stale ApprovalRequest invariant (hardening pass, P2) --------------
    //
    // "A booking that is no longer Pending cannot have an actionable Pending
    // ApprovalRequest." Before this pass, cancelling a Pending booking left
    // its ApprovalRequest at Pending forever.

    [Fact]
    public async Task CancellingAPendingBookingWithdrawsItsApprovalRequest()
    {
        var booking = Booking(BookingStatus.Pending);
        var approval = new ApprovalRequest(Guid.NewGuid(), booking.Id, NowUtc, null);
        var bookings = new FakeBookingRepository
        {
            Cancellable = booking,
            PendingApprovalRequests = [approval],
        };

        await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Withdrawn, approval.Decision);
        Assert.Equal(NowUtc, approval.DecidedAtUtc);
        Assert.Null(approval.DecidedByUserId);
    }

    // A Confirmed booking never had a live approval request to begin with —
    // the query is scoped to Pending, so this must be a no-op read, not an
    // error.
    [Fact]
    public async Task CancellingAConfirmedBookingTouchesNoApprovalRequest()
    {
        var booking = Booking(BookingStatus.Confirmed);
        var bookings = new FakeBookingRepository { Cancellable = booking };

        await Handler(bookings, Owner, Role.Member).Handle(
            new CancelBookingCommandRequest(booking.Id), CancellationToken.None);

        // No throw is the assertion; nothing here should have been queried
        // for a booking that never requested approval.
    }
}
