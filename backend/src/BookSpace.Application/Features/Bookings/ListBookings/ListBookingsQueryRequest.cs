using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.ListBookings;

// FR-4.4 read side. GET /bookings, on TenantMember: "a member can view their
// own bookings" is half of the work package's third acceptance criterion.
//
// **A top-level collection, not nested under a resource.** A member's question
// is "what have I booked", which spans every resource they have ever used —
// nesting it under /resources/{id} would make the ordinary case a fan-out of
// requests. The blackout list is nested for the opposite reason: a blackout has
// no meaning apart from the resource it blocks.
//
// Paged per decision 0015, with defaults on the constructor parameters so a
// client that sends no query string gets page 1 of 20 in chronological order.
//
// **From/To default to the whole history** (owner's call, 2026-09-08), following
// the blackout list rather than defaulting to "upcoming". A member who wants
// what is ahead of them says so with `?from=`, and the paging plus the default
// chronological sort keep an unfiltered response bounded either way. Defaulting
// to now would have made "my bookings" quietly unable to answer "what did I book
// last month", which is a question this endpoint is the only way to ask.
//
// They are an **overlap** filter, like the blackout list's: a booking counts if
// any part of it falls inside the window, so a member asking about next week
// still sees the booking that started this Friday and runs into it.
//
// UserId and Scope are the two admin-only parameters (decision 0002). A plain
// member sending either gets ValidationFailed 400 rather than a quietly
// narrowed answer — see the validator, and BookingReadRules for the resolution.
public sealed record ListBookingsQueryRequest(
    DateTime? From = null,
    DateTime? To = null,
    BookingStatus? Status = null,
    Guid? ResourceId = null,
    Guid? UserId = null,
    BookingScope Scope = BookingScope.Own,
    int Page = PagingDefaults.Page,
    int PageSize = PagingDefaults.PageSize,
    string? Sort = null)
    : IRequest<PagedResult<ListBookingsQueryResponse>>, IPagedQuery;
