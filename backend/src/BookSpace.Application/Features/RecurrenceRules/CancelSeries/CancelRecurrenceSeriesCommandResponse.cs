namespace BookSpace.Application.Features.RecurrenceRules.CancelSeries;

// The 200 body of POST /recurrence-rules/{id}/cancel. Per-endpoint and in its
// own file per decision 0015's amendment.
//
// CancelledBookingIds, not a bare count: a client cancelling a series (a
// calendar view, say) needs to know exactly which occurrences it can stop
// showing as booked, the same reasoning CancelBookingCommandResponse's
// freed interval gives for a single booking — re-reading GET /bookings would
// answer a different question (what is booked *now*, not what this action
// just freed).
public sealed record CancelRecurrenceSeriesCommandResponse(
    Guid RecurrenceRuleId,
    Guid CancelledByUserId,
    DateTime CancelledAtUtc,
    IReadOnlyList<Guid> CancelledBookingIds);
