using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;

// FR-3.4 plus decision 0001, which names this case explicitly: a blackout
// "created (or edited to a wider range)" cancels every occurrence it overlaps.
//
// Same ordering discipline as its siblings: every rule check runs before any
// mutation, so a rejected request leaves the request-scoped DbContext untouched.
//
// **Hardening pass, P0.** Same fix as CreateBlackoutPeriodCommandRequestHandler,
// for the identical race over the *new* interval: see that handler's header
// and IBlackoutPeriodRepository.LockBookingRangeAsync.
public sealed class UpdateBlackoutPeriodCommandRequestHandler
    : IRequestHandler<UpdateBlackoutPeriodCommandRequest, UpdateBlackoutPeriodCommandResponse>
{
    private readonly IBlackoutPeriodRepository _blackouts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public UpdateBlackoutPeriodCommandRequestHandler(
        IBlackoutPeriodRepository blackouts,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IClock clock)
    {
        _blackouts = blackouts;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<UpdateBlackoutPeriodCommandResponse> Handle(
        UpdateBlackoutPeriodCommandRequest request,
        CancellationToken cancellationToken)
    {
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: UpdatedByUserId is required (FR-3.4 audit trail).");

        var startsAtUtc = request.StartsAtUtc.ToUniversalTime();
        var endsAtUtc = request.EndsAtUtc.ToUniversalTime();

        // The resource first, so a wrong resource id is ResourceNotFound rather
        // than BlackoutPeriodNotFound — the two 404s say different things about
        // the same URL, and an admin debugging a script needs to know which half
        // of the path is wrong.
        var resource = await _blackouts.FindOwningResourceAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // FR-3.5. An archived resource accepts no writes at all, which is the
        // simple rule and the one its siblings follow. It also refuses the delete
        // — see DeleteBlackoutPeriodCommandRequestHandler for why that is
        // consistent rather than merely symmetrical.
        ResourceWriteRules.EnsureNotArchived(resource);

        // Scoped to the resource in the route as well as to the tenant, so a real
        // blackout id belonging to a different resource is a 404 rather than an
        // edit applied to the wrong room.
        var blackout = await _blackouts.FindForUpdateAsync(
                request.ResourceId, request.BlackoutPeriodId, cancellationToken)
            ?? throw new BlackoutPeriodNotFoundException(request.BlackoutPeriodId, request.ResourceId);

        var nowUtc = _clock.UtcNow;

        // Checked against the *new* interval only. A blackout created last week
        // for tomorrow is allowed to have elapsed since; what is refused is
        // *moving* one entirely into the past, which blocks nothing and could
        // only reach backwards into bookings that already happened.
        BlackoutPeriodRules.EnsureNotElapsed(endsAtUtc, nowUtc);

        // Called once, outside the retryable delegate: the revision is a plain
        // property update on an already-tracked entity, not staged for a
        // second time on retry, so there is nothing unsafe about running it
        // unconditionally before IUnitOfWork.ExecuteAsync opens.
        blackout.Revise(startsAtUtc, endsAtUtc, request.Reason, actorUserId, nowUtc);

        return await _unitOfWork.ExecuteAsync(
            async token =>
            {
                // P0: the lock, over the *new* interval, taken before the
                // read it protects — see CreateBlackoutPeriodCommandRequestHandler's
                // header.
                await _blackouts.LockBookingRangeAsync(blackout.ResourceId, startsAtUtc, endsAtUtc, token);

                // Decision 0001, re-run over the new interval. Read after the
                // lock and the revision so the cascade sees the interval the
                // blackout now has and nothing committed into that range after
                // the lock was taken can be missed, and before the save so both
                // land in one transaction.
                //
                // Forwards only: bookings the previous interval already
                // cancelled stay cancelled, because a cancellation is
                // irreversible — see the command for why restoring one is not
                // on the table. A narrowed or moved blackout therefore cancels
                // what it now covers and un-cancels nothing.
                var bookings = await _blackouts.FindBookingsToCancelAsync(
                    blackout.ResourceId, startsAtUtc, endsAtUtc, nowUtc, token);

                // Hardening pass, P2: see CreateBlackoutPeriodCommandRequestHandler's
                // identical comment.
                var pendingApprovals = await _blackouts.FindPendingApprovalRequestsAsync(
                    bookings.Select(b => b.Id).ToList(), token);

                var cascade = BlackoutCascade.Apply(
                    blackout,
                    bookings,
                    pendingApprovals.ToDictionary(a => a.BookingId),
                    actorUserId,
                    nowUtc);
                _blackouts.AddNotifications(cascade.Notifications);

                await _blackouts.SaveChangesAsync(token);

                return new UpdateBlackoutPeriodCommandResponse(
                    blackout.Id,
                    blackout.ResourceId,
                    blackout.StartsAtUtc,
                    blackout.EndsAtUtc,
                    blackout.Reason,
                    blackout.CreatedAtUtc,
                    blackout.UpdatedAtUtc,
                    cascade.CancelledBookings);
            },
            cancellationToken);
    }
}
