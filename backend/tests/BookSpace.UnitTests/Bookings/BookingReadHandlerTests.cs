using BookSpace.Application.Common.Errors;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;

namespace BookSpace.UnitTests.Bookings;

// WP-4 Phase 2a. What the two read handlers are responsible for: resolving the
// owner filter from the caller, parsing the sort, and turning an invisible
// booking into a 404.
//
// The filtering itself is a WHERE clause and is proved against a real SQL
// Server in BookingReadEndpointTests. These tests assert the *decision* — which
// BookingOwnerFilter reached the repository — because that is the half decision
// 0002 puts in the Application layer, and the half a fake can hold still.
public class BookingReadHandlerTests
{
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly Guid Colleague = Guid.NewGuid();

    private static ListBookingsQueryRequestHandler ListHandler(
        FakeBookingRepository bookings,
        params Role[] roles) =>
        new(bookings, new FixedCurrentUser(Caller, roles));

    private static GetBookingQueryRequestHandler GetHandler(
        FakeBookingRepository bookings,
        params Role[] roles) =>
        new(bookings, new FixedCurrentUser(Caller, roles));

    // ---- GET /bookings: whose rows -----------------------------------------

    [Fact]
    public async Task ListScopesAMemberToTheirOwnBookings()
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.Member).Handle(
            new ListBookingsQueryRequest(), CancellationToken.None);

        Assert.Equal(Caller, bookings.ListedOwner!.UserId);
    }

    // The parameter is refused by the validator first, so this asserts the
    // handler's independent gate rather than the client-facing message.
    [Fact]
    public async Task ListIgnoresUserIdFromANonAdmin()
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.Member).Handle(
            new ListBookingsQueryRequest(UserId: Colleague), CancellationToken.None);

        Assert.Equal(Caller, bookings.ListedOwner!.UserId);
    }

    [Fact]
    public async Task ListIgnoresTenantScopeFromANonAdmin()
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.Approver).Handle(
            new ListBookingsQueryRequest(Scope: BookingScope.Tenant), CancellationToken.None);

        Assert.Equal(Caller, bookings.ListedOwner!.UserId);
    }

    [Fact]
    public async Task ListLetsAnAdminNarrowToOneMember()
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.TenantAdmin).Handle(
            new ListBookingsQueryRequest(UserId: Colleague), CancellationToken.None);

        Assert.Equal(Colleague, bookings.ListedOwner!.UserId);
    }

    [Fact]
    public async Task ListLetsAnAdminWidenToTheWholeTenant()
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.TenantAdmin).Handle(
            new ListBookingsQueryRequest(Scope: BookingScope.Tenant), CancellationToken.None);

        Assert.Null(bookings.ListedOwner!.UserId);
    }

    [Fact]
    public async Task ListDefaultsAnAdminToTheirOwnBookings()
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.TenantAdmin).Handle(
            new ListBookingsQueryRequest(), CancellationToken.None);

        Assert.Equal(Caller, bookings.ListedOwner!.UserId);
    }

    // ---- GET /bookings: the query it passes through -------------------------

    [Fact]
    public async Task ListPassesEveryFilterThrough()
    {
        var bookings = new FakeBookingRepository();
        var from = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2027, 3, 8, 0, 0, 0, DateTimeKind.Utc);
        var resourceId = Guid.NewGuid();

        await ListHandler(bookings, Role.Member).Handle(
            new ListBookingsQueryRequest(
                From: from,
                To: to,
                Status: BookingStatus.Pending,
                ResourceId: resourceId,
                Page: 3,
                PageSize: 5),
            CancellationToken.None);

        var query = bookings.ListedQuery!;
        Assert.Equal(from, query.From);
        Assert.Equal(to, query.To);
        Assert.Equal(BookingStatus.Pending, query.Status);
        Assert.Equal(resourceId, query.ResourceId);
        Assert.Equal(3, query.Page);
        Assert.Equal(5, query.PageSize);
    }

    // No sort means the repository's own default (chronological), which is why
    // null rather than a constructed StartsAtUtc option reaches it.
    [Fact]
    public async Task ListPassesNoSortWhenNoneWasAskedFor()
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.Member).Handle(
            new ListBookingsQueryRequest(), CancellationToken.None);

        Assert.Null(bookings.ListedSort);
    }

    [Theory]
    [InlineData("startsAtUtc", BookingSortFields.StartsAtUtc, false)]
    [InlineData("-startsAtUtc", BookingSortFields.StartsAtUtc, true)]
    [InlineData("createdAtUtc", BookingSortFields.CreatedAtUtc, false)]
    [InlineData("-status", BookingSortFields.Status, true)]
    // Case-insensitive on the way in, canonical on the way out — SortOption's
    // contract, asserted here because this is the first booking consumer of it.
    [InlineData("-STARTSATUTC", BookingSortFields.StartsAtUtc, true)]
    public async Task ListParsesTheSortOntoTheWhitelist(
        string sort,
        string expectedField,
        bool expectedDescending)
    {
        var bookings = new FakeBookingRepository();

        await ListHandler(bookings, Role.Member).Handle(
            new ListBookingsQueryRequest(Sort: sort), CancellationToken.None);

        Assert.Equal(expectedField, bookings.ListedSort!.Field);
        Assert.Equal(expectedDescending, bookings.ListedSort.Descending);
    }

    // ---- GET /bookings/{id} -------------------------------------------------

    [Fact]
    public async Task GetScopesAMemberToTheirOwnBookings()
    {
        var bookings = new FakeBookingRepository { Detail = Detail() };

        await GetHandler(bookings, Role.Member).Handle(
            new GetBookingQueryRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(Caller, bookings.DetailOwner!.UserId);
    }

    [Fact]
    public async Task GetLetsAnAdminSeeAnyBookingInTheirTenant()
    {
        var bookings = new FakeBookingRepository { Detail = Detail() };

        await GetHandler(bookings, Role.TenantAdmin).Handle(
            new GetBookingQueryRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(bookings.DetailOwner!.UserId);
    }

    [Fact]
    public async Task GetReturnsTheBookingItWasGiven()
    {
        var detail = Detail();
        var bookings = new FakeBookingRepository { Detail = detail };

        var result = await GetHandler(bookings, Role.Member).Handle(
            new GetBookingQueryRequest(detail.Id), CancellationToken.None);

        Assert.Equal(detail, result);
        Assert.Equal(detail.Id, bookings.RequestedDetailId);
    }

    // The one branch in the handler: an invisible booking is a 404, and the
    // repository's null is what makes all three cases (no such id, another
    // tenant's, another member's) arrive identically.
    [Fact]
    public async Task GetThrowsBookingNotFoundWhenTheBookingIsNotVisible()
    {
        var bookings = new FakeBookingRepository { Detail = null };
        var bookingId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<BookingNotFoundException>(
            () => GetHandler(bookings, Role.Member).Handle(
                new GetBookingQueryRequest(bookingId), CancellationToken.None));

        Assert.Equal(ErrorKind.NotFound, exception.Kind);
        Assert.Equal(ReasonCodes.BookingNotFound, exception.ReasonCode);
    }

    // ---- No principal is a wiring bug, not a client error -------------------

    // A 500, deliberately: both endpoints sit behind TenantMember, so a request
    // that reached them has a principal. It matters that this throws rather than
    // defaulting — the caller's id *is* a member's owner filter, so a null
    // treated as "no filter" would show them the whole tenant.
    [Fact]
    public async Task ListRefusesToRunWithoutAnAuthenticatedUser()
    {
        var handler = new ListBookingsQueryRequestHandler(
            new FakeBookingRepository(), new FixedCurrentUser(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new ListBookingsQueryRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task GetRefusesToRunWithoutAnAuthenticatedUser()
    {
        var handler = new GetBookingQueryRequestHandler(
            new FakeBookingRepository(), new FixedCurrentUser(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new GetBookingQueryRequest(Guid.NewGuid()), CancellationToken.None));
    }

    // A member with no roles at all — the least-privileged caller the fake can
    // express — still gets a working list scoped to themselves, rather than an
    // exception or an empty filter.
    [Fact]
    public async Task ListWorksForACallerWithNoRoles()
    {
        var bookings = new FakeBookingRepository();

        var result = await ListHandler(bookings).Handle(
            new ListBookingsQueryRequest(), CancellationToken.None);

        Assert.Equal(Caller, bookings.ListedOwner!.UserId);
        Assert.Empty(result.Items);
    }

    private static GetBookingQueryResponse Detail() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Conference Room A",
            Caller,
            RecurrenceRuleId: null,
            new DateTime(2027, 3, 11, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 3, 11, 10, 0, 0, DateTimeKind.Utc),
            Quantity: 1,
            Title: "Design review",
            BookingStatus.Confirmed,
            CheckedInAtUtc: null,
            CancelledByUserId: null,
            CancelledAtUtc: null,
            CancellationReason: null,
            new DateTime(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc));
}
