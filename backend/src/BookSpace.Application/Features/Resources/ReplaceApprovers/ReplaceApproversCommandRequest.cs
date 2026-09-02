using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ReplaceApprovers;

// FR-3.3, TenantAdmin only. PUT /resources/{id}/approvers.
//
// Replace-the-set, symmetric with the availability windows next door, and for a
// reason of its own: per-row POST/DELETE would force an admin swapping one
// approver for another to empty the list first, and an empty list on a resource
// that requires approval is exactly the state ApproversRequired refuses. A whole
// new set in one request has no invalid intermediate state to pass through.
// Owner's call, 2026-09-01.
//
// Bare user ids, not names or emails: the client is assigning people it already
// listed. Names come back on the response, since a Guid is not something an
// admin can check by eye.
public sealed record ReplaceApproversCommandRequest(
    Guid ResourceId,
    IReadOnlyList<Guid> ApproverUserIds) : IRequest<ReplaceApproversCommandResponse>;
