namespace BookSpace.Domain.Availability;

// What RecurrenceExpansion could resolve one occurrence date to (WP-5,
// FR-6.2). Two members, not three: this is expansion, not eligibility —
// whether the resolved instant can actually be booked (availability, a
// blackout, capacity) is BookingEligibility's question, asked afterwards, per
// occurrence. An occurrence that resolves here can still be refused there.
public enum RecurrenceOccurrenceOutcome
{
    // Both LocalStartTime and LocalEndTime resolved to real instants.
    Instant,

    // Decision 0008: LocalStartTime or LocalEndTime fell inside a
    // clocks-forward gap that day, so there is no instant to create a
    // Booking from. Skipped, not shifted.
    SkippedSpringForwardGap,
}
