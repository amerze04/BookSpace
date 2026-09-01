using BookSpace.Application.Abstractions;

using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Resources.CreateResource;

// FR-3.1: an admin publishes a resource with a type, capacity and timezone.
public sealed class CreateResourceCommandRequestHandler
    : IRequestHandler<CreateResourceCommandRequest, CreateResourceCommandResponse>
{
    private readonly IResourceRepository _resources;
    private readonly ITimeZoneCatalog _timeZones;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CreateResourceCommandRequestHandler(
        IResourceRepository resources,
        ITimeZoneCatalog timeZones,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        IClock clock)
    {
        _resources = resources;
        _timeZones = timeZones;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<CreateResourceCommandResponse> Handle(
        CreateResourceCommandRequest request,
        CancellationToken cancellationToken)
    {
        // Both come from the token, never the payload. InvalidOperationException
        // rather than an AppException on purpose: this endpoint sits behind the
        // TenantAdmin policy *and* TenantMember's orgId-claim requirement, so a
        // request that got here has both. Null means the wiring is broken, which
        // is a 500, not something to hand a client a reason code for.
        var orgId = _currentTenant.OrgId
            ?? throw new InvalidOperationException(
                "No tenant context: a resource cannot be created without an owning organization.");
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: CreatedByUserId is required (FR-3.1 audit trail).");

        ResourceWriteRules.EnsureKnownTimeZone(_timeZones, request.TimeZoneId);

        // A resource being created has no approvers yet, and none can be
        // supplied here (FR-3.3's assignment endpoint is Phase 3) — so
        // RequiresApproval = true is refused rather than silently accepted into
        // the state FR-3.3 rules out. See EnsureApproversWhenRequired.
        ResourceWriteRules.EnsureApproversWhenRequired(request.RequiresApproval, approverCount: 0);

        var nowUtc = _clock.UtcNow;
        var resource = new Resource(
            Guid.NewGuid(),
            orgId,
            request.Name,
            request.ResourceType,
            request.Capacity,
            request.TimeZoneId,
            request.RequiresApproval,
            request.MinDurationMinutes,
            request.MaxDurationMinutes,
            request.Description,
            actorUserId,
            nowUtc);

        _resources.Add(resource);
        await _resources.SaveChangesAsync(cancellationToken);

        // BookSpaceDbContext's SaveChanges guard (CLAUDE.md §4.2 mechanism 2)
        // has already refused the save if OrgId disagreed with the current
        // tenant, so nothing here has to re-check it.
        return resource.ToCreateResponse();
    }
}
