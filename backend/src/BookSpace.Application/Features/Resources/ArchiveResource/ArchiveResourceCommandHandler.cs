using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ArchiveResource;

// FR-3.5. The only WP-3 write that takes no payload — archiving is a state
// transition, not an edit.
public sealed class ArchiveResourceCommandHandler
    : IRequestHandler<ArchiveResourceCommand, ResourceDetailResponse>
{
    private readonly IResourceRepository _resources;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ArchiveResourceCommandHandler(
        IResourceRepository resources,
        ICurrentUser currentUser,
        IClock clock)
    {
        _resources = resources;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<ResourceDetailResponse> Handle(
        ArchiveResourceCommand request,
        CancellationToken cancellationToken)
    {
        // See CreateResourceCommandHandler for why this is not an AppException.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (FR-3.5 audit trail).");

        // Tenant-filtered, so another tenant's real id is a 404 here exactly as
        // it is on the read and edit paths (AC-4).
        var resource = await _resources.FindForUpdateAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // Idempotent, deliberately, and this is the one place ResourceArchived is
        // *not* thrown: archiving is a transition to a terminal state, so asking
        // for a state that already holds is not a rule violation — it is the
        // outcome the caller wanted. It also makes a retry after a dropped
        // response safe. Editing an archived resource is still refused
        // (UpdateResourceCommandHandler), because that genuinely asks for
        // something FR-3.5 does not allow.
        //
        // Returning early rather than re-archiving: a no-op should not move
        // UpdatedAtUtc, or "last changed" would start meaning "last asked about".
        if (resource.IsArchived)
        {
            return resource.ToDetailResponse();
        }

        resource.Archive(actorUserId, _clock.UtcNow);
        await _resources.SaveChangesAsync(cancellationToken);

        return resource.ToDetailResponse();
    }
}
