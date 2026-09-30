using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Bookings.CheckIn;

// WP-8 Phase 3 (docs/wp8-plan.md, decision D5). FR-9.1's counterpart: a
// member confirms they showed up, so the no-show release job (this phase's
// other half) leaves this booking alone.
//
// **Owner-only, with no TenantAdmin widening**, unlike Cancel/Reject and
// decision 0002. There is no FR asking for front-desk-style check-in on
// someone else's behalf, and inventing one would be scope creep — the same
// trap the WP-7 click-through found the booking detail screen in, just the
// opposite direction (widening a reach rather than narrowing one with no
// basis). FindForCheckInAsync takes the caller's id directly rather than a
// BookingOwnerFilter, so there is no "AnyOwner" this handler could ever be
// handed by mistake.
//
// **No IUnitOfWork, matching Cancel/Reject**: one SaveChangesAsync, already
// a transaction, and nothing here adds demand against Resources.Capacity for
// CLAUDE.md §4.1's locking protocol to protect.
public sealed class CheckInCommandRequestHandler
    : IRequestHandler<CheckInCommandRequest, CheckInCommandResponse>
{
    private readonly IBookingRepository _bookings;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CheckInCommandRequestHandler(
        IBookingRepository bookings,
        ICurrentUser currentUser,
        IClock clock)
    {
        _bookings = bookings;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<CheckInCommandResponse> Handle(
        CheckInCommandRequest request,
        CancellationToken cancellationToken)
    {
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: check-in records who arrived.");

        // Filtered in the query to the caller's own booking only, so a
        // booking this caller may not check into is never loaded — the same
        // reasoning FindForCancellationAsync gives for its own owner filter,
        // applied here with no admin-widening branch at all (decision D5).
        var booking = await _bookings.FindForCheckInAsync(request.BookingId, actorUserId, cancellationToken)
            ?? throw new BookingNotFoundException(request.BookingId);

        // Asked as a question rather than caught as an exception, so the
        // client gets a reason code instead of a 500 — Booking.CheckIn
        // re-checks the same predicate, the domain keeping its own
        // invariant rather than trusting this call site.
        if (!booking.CanBeCheckedIn())
        {
            throw new BookingNotCheckableException(booking.Id);
        }

        // Idempotent (decision D5): a repeat call while still Confirmed is a
        // no-op inside CheckIn itself, so this always reflects the true
        // CheckedInAtUtc — the fresh instant on a first call, or the
        // original one on a repeat.
        booking.CheckIn(_clock.UtcNow);

        await _bookings.SaveChangesAsync(cancellationToken);

        return new CheckInCommandResponse(
            booking.Id, booking.ResourceId, booking.UserId, booking.Status, booking.CheckedInAtUtc!.Value);
    }
}
