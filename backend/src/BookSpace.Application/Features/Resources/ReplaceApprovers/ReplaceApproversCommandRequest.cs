using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ReplaceApprovers;

// FR-3.3, TenantAdmin only. PUT /resources/{id}/approvers.
//
// Replace-the-set, symmetric with the availability windows next door, and for a
// reason of its own: per-row POST/DELETE would make an admin swapping one
// approver for another pass through an intermediate state with either both or
// neither assigned, and which one depends on the order the client happened to
// pick. A whole new set in one request has no intermediate state at all.
// Owner's call, 2026-09-01.
//
// An empty list is a legitimate request, including on a resource that requires
// approval — decision 0028; its requests then fall back to the tenant's admins.
//
// Bare user ids, not names or emails: the client is assigning people it already
// listed. Names come back on the response, since a Guid is not something an
// admin can check by eye.
public sealed record ReplaceApproversCommandRequest(
    Guid ResourceId,
    IReadOnlyList<Guid> ApproverUserIds) : IRequest<ReplaceApproversCommandResponse>;
