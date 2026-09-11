using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// FR-8.1 transactional email; FR-8.4 Attempts/LastError support retry.
// FR-9.4 / AC-6 idempotence is enforced by the DB's unique
// (BookingId, RecurrenceRuleId, OccurrenceDate, RecipientUserId, Kind)
// constraint, not here.
// All notifications are delivered by email — see
// docs/decisions/0002-tenant-admin-cancellation.md.
//
// Dual-anchor (Decision #8, docs/decisions/0008-dst-spring-forward-policy.md):
// most notifications are about a Booking. RecurrenceOccurrenceSkipped is not
// — a DST spring-forward gap means the occurrence was never created, so
// there is no Booking to point at. That kind is anchored to RecurrenceRuleId
// instead. Exactly one anchor is always set, enforced by
// CK_Notifications_HasContext in the DB and mirrored below.
//
// OccurrenceDate is optional whenever RecurrenceRuleId is set (decision
// 0026, widened from Decision #8's original "RecurrenceRuleId AND
// OccurrenceDate together"): SeriesCancelled is about the whole series, not
// any one date, and forcing an OccurrenceDate on it would invent a fact this
// notification has no business claiming.
//
// Partial audit trail: CreatedByUserId is nullable because the no-show
// release job creates NoShowReleased rows with no human actor. There is no
// UpdatedByUserId — every update to this row (dispatch attempts) comes from
// the background job, never a person, so the column would always be null.
public class Notification
{
    public Guid Id { get; private set; }
    public Guid? BookingId { get; private set; }
    public Guid? RecurrenceRuleId { get; private set; }
    public DateOnly? OccurrenceDate { get; private set; }
    public Guid RecipientUserId { get; private set; }
    public NotificationKind Kind { get; private set; }
    public DateTime SendAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public Guid? CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    // EF Core materialization only — see Organization.cs for why this is needed.
    private Notification()
    {
    }

    private Notification(
        Guid id,
        Guid? bookingId,
        Guid? recurrenceRuleId,
        DateOnly? occurrenceDate,
        Guid recipientUserId,
        NotificationKind kind,
        DateTime sendAtUtc,
        Guid? createdByUserId,
        DateTime nowUtc)
    {
        // CK_Notifications_HasContext (widened by decision 0026): exactly one
        // anchor, never neither — a Booking, or a RecurrenceRule alone
        // (SeriesCancelled) or a RecurrenceRule + OccurrenceDate
        // (RecurrenceOccurrenceSkipped).
        if (bookingId is null && recurrenceRuleId is null)
            throw new ArgumentException("A notification must reference either a Booking or a RecurrenceRule.");

        Id = id;
        BookingId = bookingId;
        RecurrenceRuleId = recurrenceRuleId;
        OccurrenceDate = occurrenceDate;
        RecipientUserId = recipientUserId;
        Kind = kind;
        SendAtUtc = sendAtUtc;
        Attempts = 0;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
    }

    public static Notification ForBooking(
        Guid id,
        Guid bookingId,
        Guid recipientUserId,
        NotificationKind kind,
        DateTime sendAtUtc,
        Guid? createdByUserId,
        DateTime nowUtc) =>
        new(id, bookingId, null, null, recipientUserId, kind, sendAtUtc, createdByUserId, nowUtc);

    // Decision #8: sent 14 days before OccurrenceDate, via the existing
    // Reminder dispatch job (CLAUDE.md §7) — no new background job. Always
    // system-initiated (CreatedByUserId null): nobody chose for this
    // occurrence to fall in a DST gap.
    public static Notification ForSkippedOccurrence(
        Guid id,
        Guid recurrenceRuleId,
        DateOnly occurrenceDate,
        Guid recipientUserId,
        DateTime sendAtUtc,
        DateTime nowUtc) =>
        new(id, null, recurrenceRuleId, occurrenceDate, recipientUserId, NotificationKind.RecurrenceOccurrenceSkipped, sendAtUtc, null, nowUtc);

    // WP-5 Phase 2, FR-5.3, decision 0026: one summary row for the whole
    // cancelled series, addressed to its owner — anchored to RecurrenceRuleId
    // alone, since it is not about any single occurrence's date.
    // createdByUserId is the actor (owner or TenantAdmin); this factory is
    // never called for a self-cancel, matching decision 0002's suppression
    // rule for the single-booking cancel.
    public static Notification ForSeriesCancelled(
        Guid id,
        Guid recurrenceRuleId,
        Guid recipientUserId,
        DateTime sendAtUtc,
        Guid createdByUserId,
        DateTime nowUtc) =>
        new(id, null, recurrenceRuleId, null, recipientUserId, NotificationKind.SeriesCancelled, sendAtUtc, createdByUserId, nowUtc);

    // FR-9.4 / AC-6: workers claim rows with UPDLOCK, READPAST — this just
    // records the outcome of one attempt, it doesn't do any locking itself.
    public void RecordSendAttempt(DateTime nowUtc, bool succeeded, string? error = null)
    {
        Attempts++;
        UpdatedAtUtc = nowUtc;
        if (succeeded)
        {
            SentAtUtc = nowUtc;
            LastError = null;
        }
        else
        {
            LastError = error;
        }
    }
}
