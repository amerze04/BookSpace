using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.GetBooking;

// FR-4.4. One booking by id, if this caller may see it.
//
// **The visibility rule is pushed into the query, not applied after the read**,
// and that is the design point of this handler. It resolves an owner filter and
// hands it to the repository, so a booking the caller may not see never comes
// back at all — leaving exactly one branch here, null → BookingNotFound. A
// load-then-compare would leave a moment where someone else's booking is
// materialized and one early return away from the wire, and would make the 404
// a thing this handler remembers to do rather than a thing the query cannot
// avoid.
//
// So all three not-found cases collapse into the same answer through the same
// code path: the id exists nowhere, it belongs to another tenant (CLAUDE.md
// §4.2's filters), or it belongs to another member and the caller is not a
// TenantAdmin (BookingReadRules). Never a 403 — that would confirm the booking
// exists and leak who is holding which resource (see BookingNotFoundException).
public sealed class GetBookingQueryRequestHandler
    : IRequestHandler<GetBookingQueryRequest, GetBookingQueryResponse>
{
    private readonly IBookingRepository _bookings;
    private readonly ICurrentUser _currentUser;

    public GetBookingQueryRequestHandler(IBookingRepository bookings, ICurrentUser currentUser)
    {
        _bookings = bookings;
        _currentUser = currentUser;
    }

    public async Task<GetBookingQueryResponse> Handle(
        GetBookingQueryRequest request,
        CancellationToken cancellationToken)
    {
        // Behind TenantMember, so null is broken wiring rather than a client
        // error — and treating it as "no owner filter" would be the fail-open
        // case BookingOwnerFilter exists to prevent.
        var callerUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: GET /bookings/{id} answers about a specific member (FR-4.4).");

        var isTenantAdmin = BookingReadRules.CanSeeOtherMembersBookings(_currentUser);

        // WP-7 Phase 6, decision 0027: an Approver may also read a booking on a
        // resource they gate, not only their own. One repository call, and only
        // when it could change the answer — a TenantAdmin already sees
        // everything, and a plain member has no reach to resolve. The same gate
        // ListBookingsQueryRequestHandler uses, minus the scope test, because a
        // detail read has no scope to ask about.
        IReadOnlyCollection<Guid>? approverResourceIds = null;
        if (!isTenantAdmin && _currentUser.IsInRole(Role.Approver))
        {
            approverResourceIds = await _bookings.FindApprovableResourceIdsAsync(
                callerUserId,
                cancellationToken);
        }

        var owner = BookingReadRules.ResolveDetailFilter(
            callerUserId,
            isTenantAdmin,
            approverResourceIds);

        var detail = await _bookings.FindDetailAsync(request.BookingId, owner, cancellationToken)
            ?? throw new BookingNotFoundException(request.BookingId);

        // A second query, regardless of whether this booking's resource ever
        // required approval — the same cost WP-3 Phase 3 already accepted for
        // GET /resources/{id}'s approver names, for the same reason: joining
        // ApprovalRequests into FindDetailAsync's own query would mean the
        // common case (no approval, ever) paying for a LEFT JOIN it never uses.
        // Null here means exactly what it means on the DTO: no approval was
        // ever required.
        var approvalRequest = await _bookings.FindApprovalRequestAsync(detail.Id, cancellationToken);

        return approvalRequest is null
            ? detail
            : detail with
            {
                Approval = new GetBookingApprovalDetail(
                    approvalRequest.Id,
                    approvalRequest.RequestedAtUtc,
                    approvalRequest.ExpiresAtUtc,
                    approvalRequest.Decision,
                    approvalRequest.DecidedByUserId,
                    approvalRequest.DecidedAtUtc,
                    approvalRequest.Note),
            };
    }
}
