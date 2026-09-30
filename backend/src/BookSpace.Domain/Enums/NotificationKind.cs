namespace BookSpace.Domain.Enums;

public enum NotificationKind
{
    Confirmed,
    Rejected,
    Cancelled,
    Reminder,
    ApprovalRequested,
    NoShowReleased,

    // WP-8 Phase 4, FR-9.3, decision D3: nobody decided a Pending
    // ApprovalRequest before its ExpiresAtUtc. Deliberately its own kind
    // rather than reusing Rejected — a human saying no and nobody looking in
    // time are different facts, and this catalogue's whole philosophy (§6)
    // is not to conflate distinct causes behind one label. The *booking's*
    // Status still only has room for Rejected (Booking.ExpireApproval); this
    // is what lets the notification explaining it stay precise.
    ApprovalExpired,

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
