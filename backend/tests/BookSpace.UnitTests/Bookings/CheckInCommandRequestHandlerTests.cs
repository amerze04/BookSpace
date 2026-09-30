using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Bookings.CheckIn;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Bookings;

// WP-8 Phase 3 (docs/wp8-plan.md, decision D5). What the check-in handler
// decides: whose booking it may touch (strictly the caller's own — no
// TenantAdmin widening, unlike CancelBookingCommandRequestHandlerTests),
// whether it can still be checked into, and that a repeat call is a no-op
// rather than a refusal.
public class CheckInCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2027, 3, 1, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2027, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static Booking Booking(BookingStatus status = BookingStatus.Confirmed) =>
        new(Guid.NewGuid(), OrgId, ResourceId, Owner, null, Starts, Ends, 1, "Design review", status, Owner, NowUtc);

    private static CheckInCommandRequestHandler Handler(
        FakeBookingRepository bookings, Guid actorUserId, params Role[] roles) =>
        new(bookings, new FixedCurrentUser(actorUserId, roles), new TestClock(NowUtc));

    // ---- The happy path ----------------------------------------------------

    [Fact]
    public async Task TheOwnerChecksInTheirOwnBooking()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { CheckInable = booking };

        var response = await Handler(bookings, Owner, Role.Member).Handle(
            new CheckInCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(NowUtc, booking.CheckedInAtUtc);
        Assert.Equal(BookingStatus.Confirmed, response.Status);
        Assert.Equal(NowUtc, response.CheckedInAtUtc);
        Assert.Equal(ResourceId, response.ResourceId);
        Assert.Equal(Owner, response.UserId);
        Assert.Equal(1, bookings.SaveChangesCount);
    }

    [Fact]
    public async Task TheRepositoryIsAskedForTheCallersOwnBookingOnly()
    {
        var booking = Booking();
        var bookings = new FakeBookingRepository { CheckInable = booking };

        await Handler(bookings, Owner, Role.Member).Handle(
            new CheckInCommandRequest(booking.Id), CancellationToken.None);

        Assert.Equal(booking.Id, bookings.RequestedCheckInId);
        Assert.Equal(Owner, bookings.CheckInOwnerUserId);
    }

    // Decision D5: no admin widening at all, unlike Cancel. Even a
    // TenantAdmin's own id is passed as the plain owner argument, never
    // dropped the way Cancel's owner filter drops for them.
    [Fact]
    public async Task AnAdminStillOnlyReachesTheirOwnBookingThroughThisEndpoint()
    {
        var bookings = new FakeBookingRepository { CheckInable = null };

        await Assert.ThrowsAsync<BookingNotFoundException>(
            () => Handler(bookings, Admin, Role.TenantAdmin).Handle(
                new CheckInCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(Admin, bookings.CheckInOwnerUserId);
    }

    // ---- Idempotence (decision D5) -----------------------------------------

    [Fact]
    public async Task ARepeatCheckInIsANoOpRatherThanARefusal()
    {
        var booking = Booking();
        var firstCallClock = new TestClock(NowUtc);
        var bookings = new FakeBookingRepository { CheckInable = booking };

        await new CheckInCommandRequestHandler(bookings, new FixedCurrentUser(Owner, Role.Member), firstCallClock)
            .Handle(new CheckInCommandRequest(booking.Id), CancellationToken.None);

        var secondCallClock = new TestClock(NowUtc.AddMinutes(30));
        var response = await new CheckInCommandRequestHandler(
                bookings, new FixedCurrentUser(Owner, Role.Member), secondCallClock)
            .Handle(new CheckInCommandRequest(booking.Id), CancellationToken.None);

        // The original instant survives, not the second call's clock.
        Assert.Equal(NowUtc, booking.CheckedInAtUtc);
        Assert.Equal(NowUtc, response.CheckedInAtUtc);
        Assert.Equal(2, bookings.SaveChangesCount);
    }

    // ---- The refusals -------------------------------------------------------

    [Fact]
    public async Task AnInvisibleBookingIsBookingNotFound()
    {
        var bookings = new FakeBookingRepository { CheckInable = null };

        var exception = await Assert.ThrowsAsync<BookingNotFoundException>(
            () => Handler(bookings, Owner, Role.Member).Handle(
                new CheckInCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorKind.NotFound, exception.Kind);
        Assert.Equal(ReasonCodes.BookingNotFound, exception.ReasonCode);
        Assert.Equal(0, bookings.SaveChangesCount);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.NoShow)]
    public async Task ABookingNotCurrentlyConfirmedIsBookingNotCheckable(BookingStatus status)
    {
        var booking = Booking(status);
        var bookings = new FakeBookingRepository { CheckInable = booking };

        var exception = await Assert.ThrowsAsync<BookingNotCheckableException>(
            () => Handler(bookings, Owner, Role.Member).Handle(
                new CheckInCommandRequest(booking.Id), CancellationToken.None));

        Assert.Equal(ErrorKind.Conflict, exception.Kind);
        Assert.Equal(ReasonCodes.BookingNotCheckable, exception.ReasonCode);
        Assert.Equal(0, bookings.SaveChangesCount);
    }

    // A 500, deliberately: the endpoint sits behind TenantMember, so a
    // request that reached it has a principal.
    [Fact]
    public async Task ItRefusesToRunWithoutAnAuthenticatedUser()
    {
        var handler = new CheckInCommandRequestHandler(
            new FakeBookingRepository { CheckInable = Booking() },
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new CheckInCommandRequest(Guid.NewGuid()), CancellationToken.None));
    }
}
