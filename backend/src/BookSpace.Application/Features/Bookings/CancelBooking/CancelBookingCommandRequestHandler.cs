using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.CancelBooking;

// FR-4.4 / decision 0002: cancel a booking and free the slot.
//
// **Why this is not a §4.1 write path.** It writes to Bookings through EF, which
// dbo.CreateBooking otherwise owns, and that is deliberate: §4.1 exists because
// only a write that *adds* demand can breach Resources.Capacity, and cancelling
// can only reduce the units held at an instant. There is nothing for the
// locking protocol to protect — no lock, no procedure, and no capacity check.
// BlackoutCascade has been writing cancellations this way since WP-3 Phase 4 on
// the same reasoning.
//
// **And why it needs no IUnitOfWork.** The status change and the notification
// row go through one SaveChangesAsync, which is already a transaction, so
// CLAUDE.md §5's "wrap the unit of work in CreateExecutionStrategy" does not
// apply — that rule is for callers who would otherwise reach for
// BeginTransaction, and mixing raw SQL with EF is what made the create path one.
// The contrast with CreateBookingCommandRequestHandler is the clearest statement
// of what §5 is actually about.
//
// The concurrent-cancellation case is covered without any of that machinery:
// Bookings.RowVersion is a concurrency token, so EF appends
// `AND RowVersion = @original` to the UPDATE and two simultaneous cancels mean
// one gets DbUpdateConcurrencyException — already mapped to 409.
public sealed class CancelBookingCommandRequestHandler
    : IRequestHandler<CancelBookingCommandRequest, CancelBookingCommandResponse>
{
    private readonly IBookingRepository _bookings;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CancelBookingCommandRequestHandler(
        IBookingRepository bookings,
        ICurrentUser currentUser,
        IClock clock)
    {
        _bookings = bookings;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<CancelBookingCommandResponse> Handle(
        CancelBookingCommandRequest request,
        CancellationToken cancellationToken)
    {
        // Behind TenantMember, so null is broken wiring rather than a client
        // error — and here it would also mean writing a null into
        // Bookings.CancelledByUserId, which decision 0002 reserves for the
        // blackout cascade's actor-less transition.
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: a cancellation records who performed it (decision 0002).");

        // The same owner filter the reads use, so "may I see it" and "may I
        // cancel it" cannot disagree — a member gets their own bookings, a
        // TenantAdmin gets any in their tenant (decision 0002).
        var owner = BookingReadRules.ResolveDetailFilter(
            actorUserId,
            BookingReadRules.CanSeeOtherMembersBookings(_currentUser));

        // Filtered in the query, so a booking this caller may not cancel is
        // never loaded. All three cases arrive as null and leave as one
        // indistinguishable 404: no such id, another tenant's id, or another
        // member's booking when the caller is not an admin. Never a 403 — that
        // would confirm the booking exists (BookingNotFoundException).
        var booking = await _bookings.FindForCancellationAsync(
            request.BookingId, owner, cancellationToken)
            ?? throw new BookingNotFoundException(request.BookingId);

        var nowUtc = _clock.UtcNow;

        // Asked as a question rather than caught as an exception, so the client
        // gets a reason code instead of a 500. Booking.Cancel re-checks the same
        // predicate, which is the domain keeping its own invariant rather than
        // trusting this call site.
        if (!booking.CanBeCancelled(nowUtc))
        {
            throw new BookingNotCancellableException(booking.Id);
        }

        // Snapshotted before the mutation: the response reports the interval the
        // cancellation freed, and reading it afterwards would depend on Cancel
        // leaving those fields alone. It does today; this does not rely on it.
        var freed = (booking.ResourceId, booking.UserId, booking.StartsAtUtc, booking.EndsAtUtc,
            booking.Quantity, booking.Title);

        booking.Cancel(actorUserId, request.Reason, nowUtc);

        // Hardening pass, P2: a Pending booking's approval request must not
        // outlive it as an actionable Pending row — see
        // ApprovalRequest.Withdraw. Queried rather than assumed empty for a
        // Confirmed booking: the filter already narrows to Pending requests,
        // so this is a no-op read for the common (Confirmed) cancel.
        var pendingApprovals = await _bookings.FindPendingApprovalRequestsAsync(
            [booking.Id], cancellationToken);
        foreach (var approval in pendingApprovals)
        {
            approval.Withdraw(nowUtc);
        }

        // FR-8.1, rows only — nothing sends anything yet (CLAUDE.md §7), and
        // UQ_Notifications_Once is what makes the eventual send idempotent
        // (FR-9.4, AC-6). Same arrangement BlackoutCascade uses.
        //
        // **Suppressed when the owner cancels their own booking** (owner's call,
        // 2026-09-08). Decision 0002's requirement is that the *affected user* is
        // told; emailing someone the news they just made is noise, and the 200
        // body has already told them. A cancellation by anyone else — an admin
        // acting on decision 0002's authority — does enqueue one, addressed to
        // the owner rather than to the actor.
        if (actorUserId != freed.UserId)
        {
            _bookings.AddNotifications([
                Notification.ForBooking(
                    Guid.NewGuid(),
                    booking.Id,
                    freed.UserId,
                    NotificationKind.Cancelled,
                    // SendAtUtc = now: a cancellation is news, not a reminder,
                    // so it is due as soon as the dispatch job next runs.
                    nowUtc,
                    actorUserId,
                    nowUtc),
            ]);
        }

        // One save covers the status change and the notification row. A
        // notification whose booking was never cancelled, or a cancellation
        // nobody is told about, would each be a state nothing in the system
        // could repair.
        await _bookings.SaveChangesAsync(cancellationToken);

        return new CancelBookingCommandResponse(
            booking.Id,
            freed.ResourceId,
            freed.UserId,
            freed.StartsAtUtc,
            freed.EndsAtUtc,
            freed.Quantity,
            freed.Title,
            booking.Status,
            actorUserId,
            nowUtc,
            request.Reason);
    }
}
