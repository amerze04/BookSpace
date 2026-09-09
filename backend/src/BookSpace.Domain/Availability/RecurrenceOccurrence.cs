namespace BookSpace.Domain.Availability;

// One date RecurrenceExpansion walked to, and what came of it (WP-5).
//
// Interval is null exactly when Outcome is SkippedSpringForwardGap — the two
// travel together for the reason BookingApprovalDetail's header gives for its
// own pairing: a null Interval beside Instant, or a real one beside the skip,
// is a state nothing produces but a looser shape would allow. OccurrenceDate
// is kept on both, because a skip is still reported by date (the
// series-creation response, and 0008's 14-day-ahead notification, both need
// it with no Booking to hang it off).
public sealed record RecurrenceOccurrence(
    DateOnly OccurrenceDate,
    RecurrenceOccurrenceOutcome Outcome,
    UtcInterval? Interval);
