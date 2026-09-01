using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;

// FR-3.2: an admin manages a resource's recurring open hours.
//
// Same ordering discipline as UpdateResourceCommandRequestHandler — every rule
// check runs before any mutator, so a rejected request leaves the tracked
// aggregate untouched in a request-scoped DbContext.
public sealed class ReplaceAvailabilityWindowsCommandRequestHandler
    : IRequestHandler<ReplaceAvailabilityWindowsCommandRequest, ReplaceAvailabilityWindowsCommandResponse>
{
    private readonly IResourceRepository _resources;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ReplaceAvailabilityWindowsCommandRequestHandler(
        IResourceRepository resources,
        ICurrentUser currentUser,
        IClock clock)
    {
        _resources = resources;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<ReplaceAvailabilityWindowsCommandResponse> Handle(
        ReplaceAvailabilityWindowsCommandRequest request,
        CancellationToken cancellationToken)
    {
        // See CreateResourceCommandRequestHandler for why this is not an AppException.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (FR-3.2 audit trail).");

        // Tenant-filtered, so another tenant's real id arrives as null and leaves
        // as ResourceNotFound — the same answer an id that exists nowhere gets
        // (AC-4). No OrgId comparison is written here, deliberately: CLAUDE.md
        // §4.2's point is that isolation must not depend on a handler
        // remembering one. The windows themselves are covered by the same three
        // mechanisms since decision 0014, so this holds for the child rows too.
        var resource = await _resources.FindForUpdateAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // FR-3.5. An archived resource refuses this exactly as it refuses an
        // edit: a schedule is only meaningful for something bookable, and
        // "archived" has to mean more than a flag.
        ResourceWriteRules.EnsureNotArchived(resource);

        // Ids are minted here rather than in the entity, matching how the create
        // handler mints the resource's own — the Domain project generates no
        // identifiers, so a test can pin every one of them.
        var definitions = request.Windows
            .Select(w => new AvailabilityWindowDefinition(Guid.NewGuid(), w.Weekday, w.OpensAt, w.ClosesAt))
            .ToList();

        AvailabilityWindowRules.EnsureNoOverlaps(definitions);

        var replaced = resource.ReplaceAvailabilityWindows(definitions, actorUserId, _clock.UtcNow);

        // Stated as inserts explicitly. EF would otherwise mark a window it found
        // through the navigation as Modified, because its key is already set, and
        // save it as an UPDATE against a row that does not exist — see
        // IResourceRepository.AddAvailabilityWindows.
        _resources.AddAvailabilityWindows(replaced);

        await _resources.SaveChangesAsync(cancellationToken);

        // Ordered on the way out even though the request's order is preserved on
        // the way in: a weekly schedule has an obvious reading order, and a
        // client that sent Friday before Monday should not have to sort the
        // reply to display it. The read detail orders the same way, so the two
        // endpoints agree.
        return new ReplaceAvailabilityWindowsCommandResponse(
            resource.Id,
            replaced
                .OrderBy(w => w.Weekday)
                .ThenBy(w => w.OpensAt)
                .Select(w => new ReplacedAvailabilityWindow(w.Id, w.Weekday, w.OpensAt, w.ClosesAt))
                .ToList());
    }
}
