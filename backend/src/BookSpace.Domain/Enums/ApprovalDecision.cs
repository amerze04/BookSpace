namespace BookSpace.Domain.Enums;

public enum ApprovalDecision
{
    Pending,
    Approved,
    Rejected,
    Expired,

    // Hardening pass, P2: the booking this request belonged to was cancelled
    // — directly, by a blackout cascade (decision 0001), or by a whole-series
    // cancellation — while the request was still Pending. A terminal,
    // non-actionable state, on the same footing as Expired: there is no
    // decider, only a fact (see ApprovalRequest.Withdraw). Distinct from
    // Rejected, which records a person's judgment about the request itself
    // rather than the request's booking having stopped existing to decide on.
    Withdrawn,
}
