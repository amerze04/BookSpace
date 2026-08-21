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
    RecurrenceOccurrenceSkipped
}
