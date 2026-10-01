using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.RejectBooking;

// FR-7.1-7.5: an approver refuses a Pending booking.
//
// **Not a §4.1 write path, and needs no IUnitOfWork** — per the owner's
// answer to wp5-plan.md's shape question 7: rejecting removes a claim rather
// than adding one, so there is nothing for dbo.CreateBooking's locking
// protocol to protect, the same reasoning that already keeps the
// single-booking cancel and BlackoutCascade on plain EF. One
// SaveChangesAsync, which is already a transaction.
public sealed class RejectBookingCommandRequestHandler
    : IRequestHandler<RejectBookingCommandRequest, RejectBookingCommandResponse>
{
    private readonly IBookingRepository _bookings;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public RejectBookingCommandRequestHandler(
        IBookingRepository bookings,
        ICurrentUser currentUser,
        IClock clock)
    {
        _bookings = bookings;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<RejectBookingCommandResponse> Handle(
        RejectBookingCommandRequest request,
        CancellationToken cancellationToken)
    {
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: an approval decision records who made it.");

        var callerIsTenantAdmin = _currentUser.IsInRole(Role.TenantAdmin);

        var reach = await BookingApprovalReach.ResolveAsync(_bookings, _currentUser, actorUserId, cancellationToken);

        var booking = await _bookings.FindForApprovalAsync(request.BookingId, reach, cancellationToken)
            ?? throw new BookingNotFoundException(request.BookingId);

        // Asked as a question rather than caught as an exception, so the
        // client gets a reason code instead of a 500 — Booking.Reject
        // re-checks the same predicate, the domain keeping its own invariant
        // rather than trusting this call site.
        if (!booking.CanBeRejected())
        {
            throw new BookingNotPendingException(booking.Id);
        }

        // Hardening pass, finding 3, FR-9.3: loaded before the eligibility
        // check below so its deadline can be enforced before either — no
        // point checking whether this approver may still decide on a
        // request the system already considers overdue.
        var approvalRequest = await _bookings.FindApprovalRequestAsync(booking.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Booking {booking.Id} was Pending with no ApprovalRequest row (FR-7.1 invariant).");

        // Read once and reused for the check below and for the mutation
        // further down — CLAUDE.md §4.3's own convention, so the instant
        // this decision is judged against and the instant it is recorded
        // with never disagree.
        var nowUtc = _clock.UtcNow;

        // dbo.ApproveBooking's own re-check, mirrored here since reject has
        // no procedure to hold a lock — the same shape the eligibility
        // re-check just below already uses for the identical reason.
        // ExpiresAtUtc is never updated after creation, so no lock is needed
        // to read it safely; the race that matters is with the
        // stale-approval expiry job's *write* to this same Booking row,
        // which Bookings.RowVersion already catches at SaveChangesAsync
        // below (surfaced as 409 by GlobalExceptionHandler, unchanged).
        if (approvalRequest.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= nowUtc)
        {
            throw new ApprovalRequestExpiredException(booking.Id);
        }

        // Hardening pass, P2 — the same TOCTOU dbo.ApproveBooking's own
        // re-check closes, mirrored here since reject has no procedure to
        // hold a lock: re-verified immediately before the mutation rather
        // than trusted from ApprovalReach's read at the top of this method,
        // so an admin removing this caller from the resource in between has
        // an effect. Skipped for a TenantAdmin, whose reach (decision 0002)
        // never ran through ResourceApprovers. Not watertight against a
        // removal landing in the instant between this check and
        // SaveChangesAsync below — EF gives no lock to close that with
        // outside a stored procedure — but it closes the window that
        // mattered: the one between ApprovalReach resolving at the top of
        // this method and the decision actually being made, which could
        // otherwise be arbitrarily wide.
        if (!callerIsTenantAdmin)
        {
            var resourceIds = await _bookings.FindApprovableResourceIdsAsync(actorUserId, cancellationToken);
            if (!resourceIds.Contains(booking.ResourceId))
            {
                throw new ApproverNotEligibleException([actorUserId]);
            }
        }

        booking.Reject(actorUserId, nowUtc);

        approvalRequest.Decide(ApprovalDecision.Rejected, actorUserId, nowUtc, request.Note);

        _bookings.AddNotifications(
        [
            Notification.ForBooking(
                Guid.NewGuid(), booking.Id, booking.UserId, NotificationKind.Rejected, nowUtc, actorUserId, nowUtc),
        ]);

        // One save covers the status change, the decision record and the
        // notification row together — the same reasoning
        // CancelBookingCommandRequestHandler's header gives for its own save.
        await _bookings.SaveChangesAsync(cancellationToken);

        return new RejectBookingCommandResponse(booking.Id, BookingStatus.Rejected, actorUserId, nowUtc);
    }
}
