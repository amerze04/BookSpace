using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Bookings.ApproveBooking;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Bookings;

// WP-5 Phase 3. What the approve handler decides: whose booking it may touch,
// how dbo.ApproveBooking's answer maps onto reason codes, and — the retry
// hazard worth its own test — that a decision already recorded in memory from
// an earlier, aborted attempt is not re-applied a second time.
//
// What these tests deliberately do **not** cover is the lock itself: no fake
// can simulate a range lock, so AC-5's concurrency proof lives in
// ApproveBookingProcedureTests against a real SQL Server. Here the procedure
// is a fake that returns whichever result code the test wants.
public class ApproveBookingCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2027, 3, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2027, 3, 11, 10, 0, 0, DateTimeKind.Utc);

    private static Booking PendingBooking() =>
        new(Guid.NewGuid(), OrgId, ResourceId, Owner, null, Starts, Ends, 1, "Standup",
            BookingStatus.Pending, Owner, NowUtc);

    private static ApprovalRequest PendingApprovalRequest(Guid bookingId) =>
        new(Guid.NewGuid(), bookingId, NowUtc, null);

    private static ApproveBookingCommandRequestHandler Handler(
        FakeApprovalBookingRepository bookings, Guid actorUserId, params Role[] roles) =>
        new(bookings, new PassThroughUnitOfWork(), new FixedCurrentUser(actorUserId, roles), new TestClock(NowUtc));

    // ---- The happy path ------------------------------------------------------

    [Fact]
    public async Task ApprovesAReachablePendingBooking()
    {
        var booking = PendingBooking();
        var approvalRequest = PendingApprovalRequest(booking.Id);
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = approvalRequest,
        };

        var response = await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new ApproveBookingCommandRequest(booking.Id, "Looks fine"), CancellationToken.None);

        Assert.Equal(booking.Id, response.Id);
        Assert.Equal(BookingStatus.Confirmed, response.Status);
        Assert.Equal(Admin, response.DecidedByUserId);
        Assert.Equal(NowUtc, response.DecidedAtUtc);

        Assert.Equal(ApprovalDecision.Approved, approvalRequest.Decision);
        Assert.Equal(Admin, approvalRequest.DecidedByUserId);
        Assert.Equal("Looks fine", approvalRequest.Note);

        Assert.Equal(1, bookings.SaveChangesCount);
        Assert.Equal(1, bookings.ApproveAsyncCallCount);
    }

    [Fact]
    public async Task EnqueuesAConfirmationForTheBooker()
    {
        var booking = PendingBooking();
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = PendingApprovalRequest(booking.Id),
        };

        await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new ApproveBookingCommandRequest(booking.Id), CancellationToken.None);

        var notification = Assert.Single(bookings.AddedNotifications);
        Assert.Equal(NotificationKind.Confirmed, notification.Kind);
        Assert.Equal(booking.Id, notification.BookingId);
        Assert.Equal(Owner, notification.RecipientUserId);
        Assert.Equal(Admin, notification.CreatedByUserId);
    }

    // ---- Who may reach the booking (decision 0018/0002 reapplied) -----------

    [Fact]
    public async Task PassesTheResolvedReachToTheRepository()
    {
        var booking = PendingBooking();
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = PendingApprovalRequest(booking.Id),
        };

        await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new ApproveBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(booking.Id, bookings.RequestedApprovalId);
        Assert.Null(bookings.RequestedReach!.ResourceIds);
    }

    [Fact]
    public async Task AnUnreachableBookingIsBookingNotFound()
    {
        var bookings = new FakeApprovalBookingRepository { Reachable = null };

        var exception = await Assert.ThrowsAsync<BookingNotFoundException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new ApproveBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorKind.NotFound, exception.Kind);
        Assert.Equal(ReasonCodes.BookingNotFound, exception.ReasonCode);
        Assert.Equal(0, bookings.SaveChangesCount);
        Assert.Equal(0, bookings.ApproveAsyncCallCount);
    }

    // ---- The retry hazard (WP-5 Phase 1's staging bug, one door over) --------

    // If a 1205 retry re-enters the delegate, FindApprovalRequestAsync's
    // identity-mapped result (a fake stand-in for it here) can already be
    // Approved in memory from the aborted attempt. Calling Decide() again
    // would throw on a decision that is no longer Pending — this proves the
    // handler's guard skips it instead.
    [Fact]
    public async Task DoesNotReapplyADecisionAlreadyRecordedInMemory()
    {
        var booking = PendingBooking();
        var approvalRequest = PendingApprovalRequest(booking.Id);
        approvalRequest.Decide(ApprovalDecision.Approved, Admin, NowUtc, "First attempt");

        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = approvalRequest,
        };

        var response = await Handler(bookings, Admin, Role.TenantAdmin).Handle(
            new ApproveBookingCommandRequest(booking.Id, "Second attempt"), CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, response.Status);
        // The original decision's note survives — Decide() was not called
        // again, so nothing overwrote it.
        Assert.Equal("First attempt", approvalRequest.Note);
    }

    // ---- Every result code dbo.ApproveBooking can return --------------------

    [Theory]
    [InlineData(BookingApprovalResult.BookingNotPending, typeof(BookingNotPendingException))]
    [InlineData(BookingApprovalResult.ResourceNotFound, typeof(ResourceNotFoundException))]
    [InlineData(BookingApprovalResult.ResourceArchived, typeof(ResourceArchivedException))]
    [InlineData(BookingApprovalResult.BlackoutPeriod, typeof(BookingInBlackoutPeriodException))]
    [InlineData(BookingApprovalResult.SlotUnavailable, typeof(SlotUnavailableException))]
    [InlineData(BookingApprovalResult.CapacityExceeded, typeof(CapacityExceededException))]
    public async Task MapsEveryProcedureRejectionOntoItsReasonCode(BookingApprovalResult result, Type expected)
    {
        var booking = PendingBooking();
        var bookings = new FakeApprovalBookingRepository(result) { Reachable = booking };

        var exception = await Assert.ThrowsAnyAsync<AppException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new ApproveBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.IsType(expected, exception);
    }

    [Fact]
    public async Task NothingIsSavedWhenTheProcedureRefuses()
    {
        var booking = PendingBooking();
        var bookings = new FakeApprovalBookingRepository(BookingApprovalResult.SlotUnavailable)
        {
            Reachable = booking,
        };

        await Assert.ThrowsAsync<SlotUnavailableException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new ApproveBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(0, bookings.SaveChangesCount);
        Assert.Empty(bookings.AddedNotifications);
    }

    [Fact]
    public async Task CarriesTheProceduresRemainingCapacityOntoTheRejection()
    {
        var booking = PendingBooking();
        var bookings = new FakeApprovalBookingRepository(BookingApprovalResult.CapacityExceeded, remainingCapacity: 2)
        {
            Reachable = booking,
        };

        var exception = await Assert.ThrowsAsync<CapacityExceededException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new ApproveBookingCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(2, exception.RemainingCapacity);
    }

    // ---- Wiring ---------------------------------------------------------------

    [Fact]
    public async Task ThrowsWhenThereIsNoAuthenticatedUser()
    {
        var handler = new ApproveBookingCommandRequestHandler(
            new FakeApprovalBookingRepository { Reachable = PendingBooking() },
            new PassThroughUnitOfWork(),
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new ApproveBookingCommandRequest(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task RunsInsideTheUnitOfWork()
    {
        var booking = PendingBooking();
        var bookings = new FakeApprovalBookingRepository
        {
            Reachable = booking,
            ExistingApprovalRequest = PendingApprovalRequest(booking.Id),
        };
        var unitOfWork = new PassThroughUnitOfWork();
        var handler = new ApproveBookingCommandRequestHandler(
            bookings, unitOfWork, new FixedCurrentUser(Admin, Role.TenantAdmin), new TestClock(NowUtc));

        await handler.Handle(new ApproveBookingCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(1, unitOfWork.Executions);
    }
}
