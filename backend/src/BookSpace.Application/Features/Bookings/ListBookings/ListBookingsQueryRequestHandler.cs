using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.ListBookings;

// FR-4.4. Thin, like the other list handlers: resolve who may be seen, parse
// the sort, hand both to the repository.
//
// The one thing it does that ListResourcesQueryRequestHandler does not is
// **decide whose rows these are**, which is decision 0002's authorization half
// and cannot be a policy on the action — the same route serves a member and an
// admin, and the difference is in which rows come back rather than in whether
// the route may be called at all. The decision itself is BookingReadRules';
// this handler only supplies the caller.
public sealed class ListBookingsQueryRequestHandler
    : IRequestHandler<ListBookingsQueryRequest, PagedResult<ListBookingsQueryResponse>>
{
    private readonly IBookingRepository _bookings;
    private readonly ICurrentUser _currentUser;

    public ListBookingsQueryRequestHandler(IBookingRepository bookings, ICurrentUser currentUser)
    {
        _bookings = bookings;
        _currentUser = currentUser;
    }

    public Task<PagedResult<ListBookingsQueryResponse>> Handle(
        ListBookingsQueryRequest request,
        CancellationToken cancellationToken)
    {
        // An InvalidOperationException, not an AppException, for the reason every
        // other handler here gives: the endpoint sits behind TenantMember, so a
        // request that reached it has a principal and null means broken wiring —
        // a 500, not a reason code a client could act on.
        //
        // It also matters that this throws rather than defaulting: the caller's
        // id *is* the owner filter for a member, so a null quietly treated as
        // "no filter" would be the fail-open case BookingOwnerFilter exists to
        // prevent.
        var callerUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: GET /bookings answers about a specific member (FR-4.4).");

        var owner = BookingReadRules.ResolveOwnerFilter(
            callerUserId,
            BookingReadRules.CanSeeOtherMembersBookings(_currentUser),
            request.UserId,
            request.Scope);

        // The validator has already refused any value not on the whitelist, so
        // this cannot fail here; the return value is ignored rather than
        // re-reported, matching ListResourcesQueryRequestHandler. Parsing here
        // rather than threading a parsed SortOption through the query keeps the
        // query a plain wire shape.
        SortOption.TryParse(request.Sort, BookingSortFields.All, out var sort);

        return _bookings.ListAsync(request, owner, sort, cancellationToken);
    }
}
