using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.ApproveBooking;

// FR-7.1-7.5, AC-5, decision 0023 inherited whole: approve a Pending booking,
// re-checking capacity, blackout and archived-resource under the same lock
// dbo.CreateBooking uses, so approving a since-taken slot fails safely.
//
// **Why a re-check is needed even though a Pending booking already holds its
// claim.** A Pending booking counts toward the peak from the moment it is
// created, so nothing steals its capacity through the ordinary create path.
// The re-check exists for the state a moment's passage can produce anyway —
// a blackout added since, a resource archived since, or (closing WP-4's
// loose end 1) this exact booking already cancelled — and for defense in
// depth with dbo.CreateBooking's own guarantee, which this procedure inherits
// whole rather than re-deriving (docs/decisions/0023).
public sealed class ApproveBookingCommandRequestHandler
    : IRequestHandler<ApproveBookingCommandRequest, ApproveBookingCommandResponse>
{
    private readonly IBookingRepository _bookings;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public ApproveBookingCommandRequestHandler(
        IBookingRepository bookings,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IClock clock)
    {
        _bookings = bookings;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<ApproveBookingCommandResponse> Handle(
        ApproveBookingCommandRequest request,
        CancellationToken cancellationToken)
    {
        // Behind TenantMember, so null is broken wiring rather than a client
        // error.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: an approval decision records who made it.");

        // Hardening pass, P2: read once here and passed to dbo.ApproveBooking
        // as @CallerIsTenantAdmin, so the procedure's own re-check under lock
        // (below) agrees with ApprovalReach's — a TenantAdmin's reach never
        // depended on ResourceApprovers, so nothing about it can go stale.
        var callerIsTenantAdmin = _currentUser.IsInRole(Role.TenantAdmin);

        var reach = await BookingApprovalReach.ResolveAsync(_bookings, _currentUser, actorUserId, cancellationToken);

        // Filtered in the query, so a booking this caller may not decide on is
        // never loaded — a TenantAdmin, an Approver assigned to the resource,
        // or nobody else. Never a 403, which would confirm the booking exists
        // (BookingNotFoundException, AC-4's within-tenant form).
        var booking = await _bookings.FindForApprovalAsync(request.BookingId, reach, cancellationToken)
            ?? throw new BookingNotFoundException(request.BookingId);

        var nowUtc = _clock.UtcNow;

        // Built once, outside the retryable delegate — a 1205 retry re-runs
        // the whole delegate, and a freshly constructed notification Add()-ed
        // unconditionally inside it would double-insert on a retry, the same
        // hazard WP-5 Phase 1's series-creation handler found and fixed. The
        // same instance is safe to Add() again on retry: EF treats a repeat
        // Add of an already-tracked reference as a no-op rather than a
        // duplicate.
        var notification = Notification.ForBooking(
            Guid.NewGuid(), booking.Id, booking.UserId, NotificationKind.Confirmed, nowUtc, actorUserId, nowUtc);

        return await _unitOfWork.ExecuteAsync(
            async token =>
            {
                var outcome = await _bookings.ApproveAsync(
                    booking.Id, actorUserId, callerIsTenantAdmin, nowUtc, token);

                if (outcome.Result != BookingApprovalResult.Approved)
                {
                    throw Rejection(outcome, booking.Id, booking.ResourceId, actorUserId);
                }

                var approvalRequest = await _bookings.FindApprovalRequestAsync(booking.Id, token)
                    ?? throw new InvalidOperationException(
                        $"Booking {booking.Id} was Pending with no ApprovalRequest row (FR-7.1 invariant).");

                // Guarded, not unconditional — for the identical retry reason
                // as the notification above. A 1205 retry re-enters this
                // delegate and re-queries the same tracked ApprovalRequest
                // (EF's identity map returns the in-memory instance rather
                // than a fresh one); if the previous, aborted attempt had
                // already called Decide(), calling it again would throw on a
                // decision that is no longer Pending, even though nothing was
                // actually committed.
                if (approvalRequest.Decision == ApprovalDecision.Pending)
                {
                    approvalRequest.Decide(ApprovalDecision.Approved, actorUserId, nowUtc, request.Note);
                }

                _bookings.AddNotifications([notification]);

                await _bookings.SaveChangesAsync(token);

                return new ApproveBookingCommandResponse(booking.Id, BookingStatus.Confirmed, actorUserId, nowUtc);
            },
            cancellationToken);
    }

    // dbo.ApproveBooking's answer, as the exception the client sees — the
    // same mapping shape CreateBookingCommandRequestHandler's Rejection uses,
    // reusing every code but the one genuinely new one (decision 0023's
    // point: AC-5 is satisfied by reusing WP-4's reason codes, not inventing
    // parallel ones).
    private static AppException Rejection(
        BookingApprovalOutcome outcome, Guid bookingId, Guid resourceId, Guid actorUserId) =>
        outcome.Result switch
        {
            BookingApprovalResult.BookingNotPending => new BookingNotPendingException(bookingId),
            // Hardening pass, P2: the caller's own id is the only one that
            // could have failed this check — dbo.ApproveBooking's
            // eligibility re-check is by @ApproverUserId alone.
            BookingApprovalResult.ApproverNotEligible => new ApproverNotEligibleException([actorUserId]),
            BookingApprovalResult.ResourceNotFound => new ResourceNotFoundException(resourceId),
            BookingApprovalResult.ResourceArchived => new ResourceArchivedException(resourceId),
            BookingApprovalResult.BlackoutPeriod => new BookingInBlackoutPeriodException(resourceId),
            BookingApprovalResult.SlotUnavailable => new SlotUnavailableException(resourceId),
            BookingApprovalResult.CapacityExceeded =>
                new CapacityExceededException(resourceId, outcome.RemainingCapacity),
            _ => throw new InvalidOperationException(
                $"Unhandled booking approval result '{outcome.Result}'."),
        };
}
