using BookSpace.Domain.Enums;

namespace BookSpace.Domain.Entities;

// FR-7.2 decision record with optional note; FR-7.4/FR-9.3 ExpiresAtUtc
// drives the stale-approval job. No generic audit columns: RequestedAtUtc
// and DecidedAtUtc/DecidedByUserId already capture this row's entire
// lifecycle (created once, decided at most once) more precisely.
public class ApprovalRequest
{
    public Guid Id { get; private set; }
    public Guid BookingId { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public ApprovalDecision Decision { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? Note { get; private set; }

    public ApprovalRequest(Guid id, Guid bookingId, DateTime requestedAtUtc, DateTime? expiresAtUtc)
    {
        Id = id;
        BookingId = bookingId;
        RequestedAtUtc = requestedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        Decision = ApprovalDecision.Pending;
    }

    // CK_ApprovalRequests_DecisionPaired: a non-Pending decision requires DecidedAtUtc.
    public void Decide(ApprovalDecision decision, Guid decidedByUserId, DateTime nowUtc, string? note)
    {
        if (Decision != ApprovalDecision.Pending)
            throw new InvalidOperationException("This approval request has already been decided.");
        if (decision == ApprovalDecision.Pending)
            throw new ArgumentException("Decision must not be Pending.", nameof(decision));

        Decision = decision;
        DecidedByUserId = decidedByUserId;
        DecidedAtUtc = nowUtc;
        Note = note;
    }

    // FR-9.3: stale-approval expiry job — system-initiated, no human decider,
    // consistent with CK_ApprovalRequests_DecisionPaired allowing a null
    // DecidedByUserId as long as DecidedAtUtc is set.
    public void Expire(DateTime nowUtc)
    {
        if (Decision != ApprovalDecision.Pending)
            throw new InvalidOperationException("This approval request has already been decided.");

        Decision = ApprovalDecision.Expired;
        DecidedAtUtc = nowUtc;
    }

    // Hardening pass, P2: the invariant this establishes is "a booking that is
    // no longer Pending cannot have an actionable Pending ApprovalRequest".
    // Called from every path that cancels a Pending booking — single-booking
    // cancel, the blackout cascade, and whole-series cancellation — so a
    // stale row can never be picked up by an approve/reject call or a future
    // scanning job. System-initiated like Expire: there is no approver
    // decision being recorded, only the fact that the booking it was about
    // stopped existing to decide on. Silently returns rather than throwing
    // when already decided — the caller cancels a booking regardless of
    // whether its approval request happened to be decided a moment earlier
    // in the same request (WP-5's own retry-safety pattern, applied here),
    // and "already resolved, one way or another" is not a failure.
    public void Withdraw(DateTime nowUtc)
    {
        if (Decision != ApprovalDecision.Pending)
            return;

        Decision = ApprovalDecision.Withdrawn;
        DecidedAtUtc = nowUtc;
    }
}
