namespace BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;

// The 200 body of PUT /resources/{resourceId}/blackout-periods/{id}.
//
// Its own type rather than the create response's (docs/decisions/0015), and the
// fields happen to coincide because both writes run the same cascade and have to
// report it. They are free to stop coinciding: an edit could reasonably grow a
// count of bookings the *old* interval had already cancelled, which has no
// meaning on a create.
//
// CancelledBookings names only what *this* request cancelled. Bookings the
// blackout's previous interval had already cancelled are not listed — they were
// reported when it happened, and repeating them here would read as a fresh
// cancellation of meetings that have been off the calendar for a week.
public sealed record UpdateBlackoutPeriodCommandResponse(
    Guid Id,
    Guid ResourceId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    string? Reason,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<CancelledBookingSummary> CancelledBookings);
