namespace BookSpace.Domain.Enums;

public enum NotificationKind
{
    Confirmed,
    Rejected,
    Cancelled,
    Reminder,
    ApprovalRequested,
    NoShowReleased,

    // Decision #8 (docs/decisions/0008): a recurring occurrence that fell in
    // a DST spring-forward gap was skipped; anchored to RecurrenceRuleId +
    // OccurrenceDate on Notifications instead of BookingId — there is no
    // Booking. Sent 14 days ahead via the existing Reminder dispatch job.
    RecurrenceOccurrenceSkipped,

    // WP-5 Phase 2, FR-5.3: the whole remaining series was cancelled. One
    // summary row, not one per occurrence (owner's answer, 2026-09-08) —
    // anchored to RecurrenceRuleId alone, with no OccurrenceDate, since it is
    // about the series rather than any single date. This is why
    // CK_Notifications_HasContext was widened (decision 0026) to stop
    // requiring OccurrenceDate alongside RecurrenceRuleId.
    SeriesCancelled
}
