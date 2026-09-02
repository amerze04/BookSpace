using BookSpace.Domain.Common;
using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// FR-4.x booking lifecycle. The only table owning a time interval;
// occurrences of a recurring series are materialised here (FR-5.2,
// independently cancellable). Quantity = units consumed against
// Resources.Capacity — see docs/decisions/0005-capacity-semantics.md.
//
// Booking creation and approval MUST go through dbo.CreateBooking /
// dbo.ApproveBooking (CLAUDE.md §4.1) — the UPDLOCK/HOLDLOCK capacity check
// cannot be expressed in LINQ. This constructor is for reconstructing a
// Booking already persisted by the stored procedure (or for unit-testing
// domain behavior), never for a fresh INSERT via SaveChanges.
public class Booking : IAuditable, ITenantOwned
{
    public Guid Id { get; private set; }
    public Guid OrgId { get; private set; }
    public Guid ResourceId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? RecurrenceRuleId { get; private set; }
    public DateTime StartsAtUtc { get; private set; }
    public DateTime EndsAtUtc { get; private set; }
    public int Quantity { get; private set; }
    public string? Title { get; private set; }
    public BookingStatus Status { get; private set; }
    public DateTime? CheckedInAtUtc { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public DateTime CreatedAtUtc { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid? UpdatedByUserId { get; private set; }

    // EF Core materialization only — see Organization.cs for why this is needed.
    private Booking()
    {
    }

    public Booking(
        Guid id,
        Guid orgId,
        Guid resourceId,
        Guid userId,
        Guid? recurrenceRuleId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        int quantity,
        string? title,
        BookingStatus status,
        Guid createdByUserId,
        DateTime nowUtc)
    {
        if (endsAtUtc <= startsAtUtc) // CK_Bookings_Interval
            throw new ArgumentException("EndsAtUtc must be after StartsAtUtc.", nameof(endsAtUtc));
        if (quantity <= 0) // CK_Bookings_Quantity
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");

        Id = id;
        OrgId = orgId;
        ResourceId = resourceId;
        UserId = userId;
        RecurrenceRuleId = recurrenceRuleId;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        Quantity = quantity;
        Title = title;
        Status = status;
        CreatedAtUtc = nowUtc;
        CreatedByUserId = createdByUserId;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = createdByUserId;
    }

    Guid? ITenantOwned.OrgId => OrgId;

    // Decision #2: a TenantAdmin may cancel a booking they don't own —
    // actorUserId is recorded distinctly from UserId (the owner).
    public void Cancel(Guid? actorUserId, string? reason, DateTime nowUtc)
    {
        if (Status is BookingStatus.Cancelled or BookingStatus.Completed or BookingStatus.NoShow)
            throw new InvalidOperationException($"Cannot cancel a booking in status {Status}.");

        Status = BookingStatus.Cancelled;
        CancelledByUserId = actorUserId;
        CancelledAtUtc = nowUtc;
        CancellationReason = reason;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = actorUserId;
    }

    // Decision #1 (docs/decisions/0001-blackout-vs-recurring-series.md): a
    // blackout has absolute priority, so every occurrence it overlaps is
    // cancelled. Its own method rather than a call to Cancel(null, ...) because
    // 0001 asks for "a reason/actor variant distinct from a user-initiated
    // cancel", and the distinction is worth having in the type: this transition
    // has no cancelling *user*, and the guard below is stricter.
    //
    // CancelledByUserId stays null on purpose. A person did cause this — the
    // admin who created the blackout — but 0001 records that actor on
    // BlackoutPeriods.CreatedByUserId instead, so writing them here would claim
    // the booking's owner was overruled by someone acting on that booking.
    // UpdatedByUserId stays null for the same reason decision #4 leaves it null
    // on a no-show: the transition was driven by a rule, not by an edit.
    public void CancelForBlackout(string? reason, DateTime nowUtc)
    {
        if (!CanBeCancelledForBlackout(nowUtc))
            throw new InvalidOperationException(
                $"Booking {Id} in status {Status} ending {EndsAtUtc:o} cannot be cancelled by a blackout.");

        Status = BookingStatus.Cancelled;
        CancelledByUserId = null;
        CancelledAtUtc = nowUtc;
        CancellationReason = reason;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = null;
    }

    // Which occurrences a blackout may cancel, stated once here so the domain
    // owns the rule and the repository's SQL filter is only an optimization that
    // mirrors it (see IBlackoutPeriodRepository.FindBookingsToCancelAsync).
    //
    // Two conditions, and the second is the one worth explaining. Pending and
    // Confirmed are the only statuses holding a claim on the resource — the rest
    // are terminal, and Cancel() already refuses them. But `Confirmed` alone is
    // not enough, because **nothing in this system currently writes
    // BookingStatus.Completed**: there is no Complete() method and no job in
    // CLAUDE.md §7 that sets it, so a meeting that actually happened and was
    // checked into stays Confirmed indefinitely. Without the EndsAtUtc test, a
    // blackout covering last month would cancel attended meetings and stamp
    // CancelledAtUtc on history — which FR-3.5's premise (history is preserved
    // as it was) rules out.
    //
    // A booking already in progress *is* cancellable: the room is unusable from
    // now on, so the meeting in it has to stop.
    public bool CanBeCancelledForBlackout(DateTime nowUtc) =>
        Status is BookingStatus.Pending or BookingStatus.Confirmed
        && EndsAtUtc > nowUtc;

    public void CheckIn(DateTime nowUtc)
    {
        if (Status != BookingStatus.Confirmed)
            throw new InvalidOperationException($"Cannot check in a booking in status {Status}.");

        CheckedInAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = UserId;
    }

    // Decision #4: no-show = confirmed, never checked in, past the org's
    // grace period measured from StartsAtUtc. Pure predicate — the no-show
    // release job (system, not a person) evaluates this and calls MarkNoShow.
    public bool IsNoShow(DateTime nowUtc, int graceMinutes) =>
        Status == BookingStatus.Confirmed
        && CheckedInAtUtc is null
        && nowUtc > StartsAtUtc.AddMinutes(graceMinutes);

    // Decision #4: system-initiated — UpdatedByUserId stays null to record
    // that a background job, not a person, made this transition.
    public void MarkNoShow(DateTime nowUtc)
    {
        if (Status != BookingStatus.Confirmed)
            throw new InvalidOperationException($"Cannot mark a booking in status {Status} as a no-show.");

        Status = BookingStatus.NoShow;
        UpdatedAtUtc = nowUtc;
        UpdatedByUserId = null;
    }
}
