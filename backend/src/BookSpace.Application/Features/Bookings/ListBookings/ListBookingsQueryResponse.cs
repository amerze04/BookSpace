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
// RecurrenceRuleId is deliberately **not** here, and is on the detail instead.
// WP-4 never writes it, so today it would be a column that is null on every row;
// WP-5 makes occurrences independently viewable (FR-5.2) and can add it to the
// list then, which is an additive change no client breaks on.
//
// Status serializes as its name — "Confirmed", not 1 — because Program.cs
// registered JsonStringEnumConverter app-wide in WP-3 Phase 3.
public sealed record ListBookingsQueryResponse(
    Guid Id,
    Guid ResourceId,
    string ResourceName,
    Guid UserId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Quantity,
    string? Title,
    BookingStatus Status);
