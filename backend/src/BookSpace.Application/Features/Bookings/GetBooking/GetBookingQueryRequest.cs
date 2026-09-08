using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.GetBooking;

// FR-4.4 read side, one booking by id. GET /bookings/{id}, on TenantMember.
//
// A member sees their own; a TenantAdmin sees any booking in their tenant
// (decision 0002). Everything else — another member's booking, another tenant's
// booking, an id that exists nowhere — is one indistinguishable 404
// BookingNotFound, which is the whole reason this endpoint does not simply
// return 403 for the second case (see BookingNotFoundException).
//
// Its own endpoint rather than a filter on the list, because it is what the
// create response's Location header points at, and because the detail carries
// fields the list row deliberately omits.
public sealed record GetBookingQueryRequest(Guid BookingId)
    : IRequest<GetBookingQueryResponse>;
