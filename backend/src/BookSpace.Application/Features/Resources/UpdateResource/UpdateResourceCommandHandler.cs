using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.UpdateResource;

// FR-3.1 edit, FR-3.5 archived resources refuse edits.
//
// Every rule check runs before any mutator, so a rejected edit leaves the
// tracked entity untouched. That matters more than it looks: the DbContext is
// scoped to the request, so a half-applied entity that then threw would still
// be sitting in the change tracker.
public sealed class UpdateResourceCommandHandler
    : IRequestHandler<UpdateResourceCommand, UpdateResourceResponse>
{
    private readonly IResourceRepository _resources;
    private readonly ITimeZoneCatalog _timeZones;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public UpdateResourceCommandHandler(
        IResourceRepository resources,
        ITimeZoneCatalog timeZones,
        ICurrentUser currentUser,
        IClock clock)
    {
        _resources = resources;
        _timeZones = timeZones;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<UpdateResourceResponse> Handle(
        UpdateResourceCommand request,
        CancellationToken cancellationToken)
    {
        // See CreateResourceCommandHandler for why this is not an AppException.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (FR-3.1 audit trail).");

        // Tenant-filtered, so another tenant's real id arrives here as null and
        // leaves as ResourceNotFound — the same answer an id that exists
        // nowhere gets (AC-4). No OrgId comparison is written here, deliberately:
        // CLAUDE.md §4.2's whole point is that isolation must not depend on a
        // handler remembering one.
        var resource = await _resources.FindForUpdateAsync(request.ResourceId, cancellationToken)
            ?? throw new AppException(
                ErrorKind.NotFound,
                ReasonCodes.ResourceNotFound,
                $"Resource {request.ResourceId} was not found in the current tenant.");

        ResourceWriteRules.EnsureNotArchived(resource);
        ResourceWriteRules.EnsureKnownTimeZone(_timeZones, request.TimeZoneId);
        ResourceWriteRules.EnsureApproversWhenRequired(
            request.RequiresApproval, resource.ApproverUserIds.Count);

        // Only a decrease can strand an existing booking, and the check costs a
        // query — so an increase, or no change, skips it.
        if (request.Capacity < resource.Capacity)
        {
            var peakConcurrentQuantity = await _resources.PeakConcurrentBookedQuantityAsync(
                resource.Id, _clock.UtcNow, cancellationToken);

            ResourceWriteRules.EnsureCapacityCoversExistingBookings(
                request.Capacity, peakConcurrentQuantity);
        }

        var previousTimeZoneId = resource.TimeZoneId;
        var nowUtc = _clock.UtcNow;

        resource.UpdateDetails(request.Name, request.Description, request.ResourceType, actorUserId, nowUtc);
        resource.ChangeCapacity(request.Capacity, actorUserId, nowUtc);
        resource.ChangeTimeZone(request.TimeZoneId, actorUserId, nowUtc);
        resource.SetDurationLimits(request.MinDurationMinutes, request.MaxDurationMinutes, actorUserId, nowUtc);
        resource.SetRequiresApproval(request.RequiresApproval, actorUserId, nowUtc);

        await _resources.SaveChangesAsync(cancellationToken);

        // Ordinal comparison: an IANA id is case-sensitive, and the catalog has
        // already refused anything that is not the canonical spelling, so two
        // strings differing only in case cannot both have got this far.
        var timeZoneChange = string.Equals(previousTimeZoneId, resource.TimeZoneId, StringComparison.Ordinal)
            ? null
            : new TimeZoneChangeNotice(
                previousTimeZoneId,
                resource.TimeZoneId,
                resource.AvailabilityWindows.Count);

        return new UpdateResourceResponse(resource.ToDetailResponse(), timeZoneChange);
    }
}
