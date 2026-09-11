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

        var nowUtc = _clock.UtcNow;

        booking.Reject(actorUserId, nowUtc);

        var approvalRequest = await _bookings.FindApprovalRequestAsync(booking.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Booking {booking.Id} was Pending with no ApprovalRequest row (FR-7.1 invariant).");

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
