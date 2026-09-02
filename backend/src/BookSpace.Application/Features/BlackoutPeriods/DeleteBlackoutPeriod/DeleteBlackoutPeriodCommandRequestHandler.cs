using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.BlackoutPeriods.DeleteBlackoutPeriod;

// FR-3.4. Removes a blackout so it stops blocking future bookings. See the
// command for why this is a hard delete and why it un-cancels nothing.
public sealed class DeleteBlackoutPeriodCommandRequestHandler
    : IRequestHandler<DeleteBlackoutPeriodCommandRequest, Unit>
{
    private readonly IBlackoutPeriodRepository _blackouts;

    public DeleteBlackoutPeriodCommandRequestHandler(IBlackoutPeriodRepository blackouts)
    {
        _blackouts = blackouts;
    }

    public async Task<Unit> Handle(
        DeleteBlackoutPeriodCommandRequest request,
        CancellationToken cancellationToken)
    {
        // No ICurrentUser and no IClock, unlike every other write in this
        // feature: there is no row left to stamp. The actor is in the request log
        // with its correlation id, which is the only place an audit trail for a
        // deletion could live given that §4.5's usual answer (a flag) does not
        // apply here.
        var resource = await _blackouts.FindOwningResourceAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // FR-3.5, and the one place this rule is arguable: an admin might want to
        // tidy up blackouts on a resource they have just archived. Refused anyway,
        // for consistency — "an archived resource accepts no writes" is a rule an
        // admin can hold in their head, and the alternative would make deletion
        // the single exception to it. The cost is nil in practice: an archived
        // resource takes no bookings, so its blackouts block nothing and there is
        // no operational reason to remove them.
        ResourceWriteRules.EnsureNotArchived(resource);

        // Scoped to the resource in the route as well as to the tenant, so a real
        // blackout id belonging to another resource is a 404 rather than a
        // deletion from the wrong room.
        //
        // **Deliberately not idempotent.** A second DELETE of the same id is a
        // 404, not a 204. DELETE is idempotent in the sense that the end state is
        // the same, and returning 204 for an id that never existed would be
        // defensible — but this endpoint cannot tell "already deleted" from
        // "belongs to another tenant" (AC-4 forbids it), so a blanket 204 would
        // silently accept a cross-tenant id. A 404 for both is the answer that
        // stays honest. Note the contrast with POST /resources/{id}/archive,
        // which *is* idempotent: there the row is still present to inspect.
        var blackout = await _blackouts.FindForUpdateAsync(
                request.ResourceId, request.BlackoutPeriodId, cancellationToken)
            ?? throw new BlackoutPeriodNotFoundException(request.BlackoutPeriodId, request.ResourceId);

        _blackouts.Remove(blackout);
        await _blackouts.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}
