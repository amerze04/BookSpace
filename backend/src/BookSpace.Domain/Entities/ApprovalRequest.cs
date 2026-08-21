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
}
