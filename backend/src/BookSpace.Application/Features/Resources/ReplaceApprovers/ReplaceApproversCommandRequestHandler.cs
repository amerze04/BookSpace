using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ReplaceApprovers;

// FR-3.3: a resource marked RequiresApproval has one or more assigned approvers.
//
// This handler owns the *other half* of the invariant Phase 2 built. Create and
// edit already refuse RequiresApproval = true with an empty approver list; without
// the same check here an admin could set the flag with an approver assigned, then
// clear the list, and land in exactly the state FR-3.3 rules out — through the
// side door.
//
// Same ordering discipline as the sibling write handlers: every rule check runs
// before any mutator, so a rejected request leaves the tracked aggregate untouched.
public sealed class ReplaceApproversCommandRequestHandler
    : IRequestHandler<ReplaceApproversCommandRequest, ReplaceApproversCommandResponse>
{
    private readonly IResourceRepository _resources;
    private readonly IUserRepository _users;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ReplaceApproversCommandRequestHandler(
        IResourceRepository resources,
        IUserRepository users,
        ICurrentUser currentUser,
        IClock clock)
    {
        _resources = resources;
        _users = users;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<ReplaceApproversCommandResponse> Handle(
        ReplaceApproversCommandRequest request,
        CancellationToken cancellationToken)
    {
        // See CreateResourceCommandRequestHandler for why this is not an AppException.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (FR-3.3 audit trail).");

        // Tenant-filtered, so another tenant's real id arrives as null and leaves
        // as ResourceNotFound, indistinguishable from an id that exists nowhere
        // (AC-4).
        var resource = await _resources.FindForUpdateAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        ResourceWriteRules.EnsureNotArchived(resource);

        // FR-3.3's empty-state rule, applied from the approver side. The flag is
        // read from the resource rather than the payload: this endpoint does not
        // set it, so the question is whether the list being emptied belongs to a
        // resource that currently requires approval.
        ResourceWriteRules.EnsureApproversWhenRequired(
            resource.RequiresApproval, request.ApproverUserIds.Count);

        // Skipped entirely for an empty list — there is nothing to look up, and a
        // resource that does not require approval is allowed to have none.
        if (request.ApproverUserIds.Count > 0)
        {
            var eligible = await _users.FindEligibleApproverIdsAsync(
                request.ApproverUserIds, cancellationToken);

            ResourceWriteRules.EnsureEveryApproverIsEligible(request.ApproverUserIds, eligible);
        }

        resource.ReplaceApprovers(request.ApproverUserIds, actorUserId, _clock.UtcNow);

        await _resources.SaveChangesAsync(cancellationToken);

        // Re-read for the names. The eligibility query returned ids only, on
        // purpose: it answers a yes/no question about a set, and widening it to
        // carry display data would make the rule check pay for a projection that
        // the empty-list and rejected paths never use.
        var approvers = await _users.FindApproverSummariesAsync(
            request.ApproverUserIds, cancellationToken);

        return new ReplaceApproversCommandResponse(
            resource.Id,
            resource.RequiresApproval,
            approvers.Select(a => new AssignedApprover(a.UserId, a.FullName)).ToList());
    }
}
