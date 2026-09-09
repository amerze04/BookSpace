using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.ListBookings;

// One row of GET /bookings. Per-endpoint and in its own file per decision 0015's
// amendment, even though GetBookingQueryResponse repeats most of it — this is
// the row a *list* returns, and it is the one that has to stay small.
//
// A summary, not the whole aggregate: the cancellation fields, the check-in
// stamp and the audit timestamps are on the detail response instead, so a page
// of twenty bookings is not twenty copies of columns that are null on all of
// them.
//
// **ResourceName is denormalized onto the row** (owner's call, 2026-09-08),
// which is the one place this response is not simply the booking's own columns.
// A member's list spans resources by definition, so ids alone would force a
// fetch per row to render anything a person could read. One join, projected in
// the same query.
//
// RecurrenceRuleId is now here too (WP-5 Phase 2, FR-5.2) — WP-4 left it off
// because it was null on every row; now that a series populates it, a member
// browsing their list needs to see which bookings belong to one without a
// second request per row, the same reasoning that put ResourceName here.
//
// Status serializes as its name — "Confirmed", not 1 — because Program.cs
// registered JsonStringEnumConverter app-wide in WP-3 Phase 3.
public sealed record ListBookingsQueryResponse(
    Guid Id,
    Guid ResourceId,
    string ResourceName,
    Guid UserId,
    Guid? RecurrenceRuleId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Quantity,
    string? Title,
    BookingStatus Status);
