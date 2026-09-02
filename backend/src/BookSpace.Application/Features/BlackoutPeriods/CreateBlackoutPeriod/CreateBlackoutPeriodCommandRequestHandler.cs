using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;

// FR-3.4: an admin blacks out a span during which a resource cannot be booked,
// and it overrides availability. Plus decision 0001's cascade, which is most of
// the work — see BlackoutCascade.
//
// Same ordering discipline as the Phase 2 and 3 write handlers: every rule check
// runs before any mutation, so a rejected request leaves the request-scoped
// DbContext untouched and nothing has to be undone.
public sealed class CreateBlackoutPeriodCommandRequestHandler
    : IRequestHandler<CreateBlackoutPeriodCommandRequest, CreateBlackoutPeriodCommandResponse>
{
    private readonly IBlackoutPeriodRepository _blackouts;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CreateBlackoutPeriodCommandRequestHandler(
        IBlackoutPeriodRepository blackouts,
        ICurrentUser currentUser,
        IClock clock)
    {
        _blackouts = blackouts;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<CreateBlackoutPeriodCommandResponse> Handle(
        CreateBlackoutPeriodCommandRequest request,
        CancellationToken cancellationToken)
    {
        // See CreateResourceCommandRequestHandler for why this is an
        // InvalidOperationException and not an AppException: the endpoint sits
        // behind the TenantAdmin policy, so a request that got here has a
        // principal, and null means broken wiring — a 500, not a reason code.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: CreatedByUserId is required (FR-3.4 audit trail).");

        // Normalized before anything reads them. The validator has already
        // refused DateTimeKind.Unspecified, so this only ever converts a
        // client-supplied offset; a UTC value is returned unchanged.
        var startsAtUtc = request.StartsAtUtc.ToUniversalTime();
        var endsAtUtc = request.EndsAtUtc.ToUniversalTime();

        // Tenant-filtered, so another tenant's real resource id arrives as null
        // and leaves as ResourceNotFound — the same answer an id that exists
        // nowhere gets (AC-4). No OrgId comparison is written here, deliberately:
        // CLAUDE.md §4.2's point is that isolation must not depend on a handler
        // remembering one.
        var resource = await _blackouts.FindOwningResourceAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // FR-3.5. An archived resource refuses this exactly as it refuses an edit
        // or a schedule change: it accepts no new bookings, so blocking time on
        // it would describe a restriction on something already fully restricted.
        ResourceWriteRules.EnsureNotArchived(resource);

        var nowUtc = _clock.UtcNow;
        BlackoutPeriodRules.EnsureNotElapsed(endsAtUtc, nowUtc);

        // OrgId is taken from the resource the caller just loaded, never from the
        // request. FK_BlackoutPeriods_Resources_SameOrg makes the two physically
        // unable to disagree anyway (decision 0014), and BookSpaceDbContext's
        // SaveChanges guard would refuse a mismatch with the current tenant
        // (§4.2 mechanism 2) — this is simply the value that satisfies both.
        var blackout = new BlackoutPeriod(
            Guid.NewGuid(),
            resource.OrgId,
            resource.Id,
            startsAtUtc,
            endsAtUtc,
            request.Reason,
            actorUserId,
            nowUtc);

        _blackouts.Add(blackout);

        // Decision 0001. Read after the blackout is staged but before the save,
        // so both land in one transaction: a cancellation that committed without
        // its blackout, or a blackout without its cancellations, would leave the
        // absolute-priority guarantee broken with nothing to point at.
        var bookings = await _blackouts.FindBookingsToCancelAsync(
            resource.Id, startsAtUtc, endsAtUtc, nowUtc, cancellationToken);

        var cascade = BlackoutCascade.Apply(blackout, bookings, actorUserId, nowUtc);
        _blackouts.AddNotifications(cascade.Notifications);

        // One save for the blackout, the cancellations and the notifications. No
        // explicit transaction: SaveChanges is already transactional, so
        // CLAUDE.md §5's execution-strategy rule does not apply here (see
        // IBlackoutPeriodRepository).
        await _blackouts.SaveChangesAsync(cancellationToken);

        return new CreateBlackoutPeriodCommandResponse(
            blackout.Id,
            blackout.ResourceId,
            blackout.StartsAtUtc,
            blackout.EndsAtUtc,
            blackout.Reason,
            blackout.CreatedAtUtc,
            cascade.CancelledBookings);
    }
}
