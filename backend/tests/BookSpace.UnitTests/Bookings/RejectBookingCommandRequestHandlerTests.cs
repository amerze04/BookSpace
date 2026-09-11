using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Bookings.RejectBooking;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Bookings;

// WP-5 Phase 3. Reject needs no procedure and no lock (rejecting removes a
// claim rather than adding one), so this is a much shorter test file than
// ApproveBookingCommandRequestHandlerTests — the reach resolution it shares
// with approve is covered by BookingApprovalReachTests.
public class RejectBookingCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2027, 3, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2027, 3, 11, 10, 0, 0, DateTimeKind.Utc);

    private static Booking Booking(BookingStatus status = BookingStatus.Pending) =>
        new(Guid.NewGuid(), OrgId, ResourceId, Owner, null, Starts, Ends, 1, "Standup", status, Owner, NowUtc);

    private static RejectBookingCommandRequestHandler Handler(
        FakeApprovalBookingRepository bookings, Guid actorUserId, params Role[] roles) =>
        new(bookings, new FixedCurrentUser(actorUserId, roles), new TestClock(NowUtc));

    [Fact]
    public async Task RejectsAReachablePendingBooking()
    {
        var booking = Booking();
        var approvalRequest = new ApprovalRequest(Guid.NewGuid(), booking.Id, NowUtc, null);
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = approvalRequest,
        };

        var response = await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new RejectBookingCommandRequest(booking.Id, "No longer needed"), CancellationToken.None);

        Assert.Equal(BookingStatus.Rejected, booking.Status);
        Assert.Equal(Admin, booking.UpdatedByUserId);

        Assert.Equal(ApprovalDecision.Rejected, approvalRequest.Decision);
        Assert.Equal("No longer needed", approvalRequest.Note);

        Assert.Equal(BookingStatus.Rejected, response.Status);
        Assert.Equal(Admin, response.DecidedByUserId);
        Assert.Equal(1, bookings.SaveChangesCount);
    }

    [Fact]
    public async Task EnqueuesARejectionNotificationForTheBooker()
    {
        var booking = Booking();
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = new ApprovalRequest(Guid.NewGuid(), booking.Id, NowUtc, null),
        };

        await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new RejectBookingCommandRequest(booking.Id), CancellationToken.None);

        var notification = Assert.Single(bookings.AddedNotifications);
        Assert.Equal(NotificationKind.Rejected, notification.Kind);
        Assert.Equal(Owner, notification.RecipientUserId);
        Assert.Equal(Admin, notification.CreatedByUserId);
    }

    [Fact]
    public async Task PassesTheResolvedReachToTheRepository()
    {
        var bookings = new FakeApprovalBookingRepository { Reachable = null };

        await Assert.ThrowsAsync<BookingNotFoundException>(
            () => Handler(bookings, Admin, Role.Approver).Handle(
                new RejectBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.NotNull(bookings.RequestedReach);
    }

    [Fact]
    public async Task AnUnreachableBookingIsBookingNotFound()
    {
        var bookings = new FakeApprovalBookingRepository { Reachable = null };

        var exception = await Assert.ThrowsAsync<BookingNotFoundException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new RejectBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorKind.NotFound, exception.Kind);
        Assert.Equal(0, bookings.SaveChangesCount);
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public async Task ANonPendingBookingIsBookingNotPending(BookingStatus status)
    {
        var booking = Booking(status);
        var bookings = new FakeApprovalBookingRepository { Reachable = booking };

        var exception = await Assert.ThrowsAsync<BookingNotPendingException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new RejectBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(ReasonCodes.BookingNotPending, exception.ReasonCode);
    }

    [Fact]
    public async Task NothingIsSavedWhenTheBookingIsNotPending()
    {
        var booking = Booking(BookingStatus.Confirmed);
        var bookings = new FakeApprovalBookingRepository { Reachable = booking };

        await Assert.ThrowsAsync<BookingNotPendingException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new RejectBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(0, bookings.SaveChangesCount);
        Assert.Empty(bookings.AddedNotifications);
        // Confirmed to begin with, and still Confirmed — a refusal must not
        // have mutated the entity before throwing.
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
    }

    // Hardening pass, P2 — approver TOCTOU, reject's half. Reachable is set
    // directly (simulating ApprovalReach having resolved eligibility a
    // moment ago, before an admin removed this caller from the resource),
    // but ApprovableResourceIds — the re-check this pass added, queried
    // fresh immediately before the mutation — no longer contains ResourceId.
    // Before this pass, nothing re-verified eligibility here at all, and the
    // rejection would have gone through.
    [Fact]
    public async Task RefusesAnApproverNoLongerAssignedToTheResource()
    {
        var booking = Booking();
        var approverId = Guid.NewGuid();
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ApprovableResourceIds = [], // no longer includes booking's ResourceId
        };

        var exception = await Assert.ThrowsAsync<ApproverNotEligibleException>(
            () => Handler(bookings, approverId, Role.Approver).Handle(
                new RejectBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(ReasonCodes.ApproverNotEligible, exception.ReasonCode);

        // Refused before any mutation or save — the same "nothing partially
        // applied" discipline ANonPendingBookingIsBookingNotPending already
        // asserts for the sibling rejection.
        Assert.Equal(BookingStatus.Pending, booking.Status);
        Assert.Equal(0, bookings.SaveChangesCount);
    }

    // The other half: a TenantAdmin's reach does not depend on
    // ApprovableResourceIds at all, so an empty set changes nothing for them.
    [Fact]
    public async Task ATenantAdminNeedsNoApprovableResourceIdsEntry()
    {
        var booking = Booking();
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = new ApprovalRequest(Guid.NewGuid(), booking.Id, NowUtc, null),
            ApprovableResourceIds = [],
        };

        var response = await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new RejectBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Rejected, response.Status);
    }

    [Fact]
    public async Task ThrowsWhenThereIsNoAuthenticatedUser()
    {
        var handler = new RejectBookingCommandRequestHandler(
            new FakeApprovalBookingRepository { Reachable = Booking() },
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new RejectBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));
    }
}
