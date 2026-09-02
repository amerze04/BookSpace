namespace BookSpace.Application.Features.Resources.ReplaceApprovers;

// One approver as the PUT response reports them. Its own type rather than the
// read detail's ApproverDetail (convention agreed 2026-09-01,
// docs/decisions/0015) — identical today, and free to diverge: the read detail is
// the one a member sees, so anything eligibility-related worth showing an admin
// after a write belongs here and not there.
public sealed record AssignedApprover(Guid UserId, string FullName);
