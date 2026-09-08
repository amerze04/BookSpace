using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings.GetBooking;

// GET /bookings/{id}, 200. Per-endpoint and in its own file per decision 0015's
// amendment, even though it shares most fields with ListBookingsQueryResponse
// and CreateBookingCommandResponse — this is the one a client polls for a
// booking's current state, so it is the one that has to carry the fields that
// only become interesting after creation.
//
// Beyond the list row, it adds:
//
//   RecurrenceRuleId — null for every booking WP-4 creates, and on the wire
//   anyway: it is what will distinguish a series occurrence from a one-off, and
//   FR-5.2 makes each occurrence independently viewable through exactly this
//   endpoint. A client that learns to read it now needs no change in WP-5.
//
//   CheckedInAtUtc — FR-4.x check-in. Nothing writes it yet (§7's no-show job is
//   out of WP-4's scope), so it is null in practice today.
//
//   The three cancellation fields — who called it off, when, and why. They are
//   the record decision 0002 asks for, and CancelledByUserId differing from
//   UserId is precisely how a member sees that an admin cancelled their booking
//   rather than themselves. CancelledByUserId is also null on a booking a
//   blackout cancelled (Booking.CancelForBlackout leaves it null deliberately),
//   in which case CancellationReason is the text snapshot naming the blackout.
//
//   CreatedAtUtc / UpdatedAtUtc — the audit pair every detail read here carries.
//
// ResourceName is denormalized on, exactly as on the list row (owner's call,
// 2026-09-08), so a client rendering one booking needs no second request to name
// the thing that was booked.
//
// **No approval detail**, deliberately, even for a Pending booking: the
// ApprovalRequest row exists (FR-7.1) but nothing can act on it until WP-5's
// approve/reject endpoints, and the shape it should take on the wire is that
// package's to decide alongside the approver queue. Status = Pending already
// tells a client the booking is waiting.
//
// The status and both enums serialize as names, not ordinals — Program.cs
// registered JsonStringEnumConverter app-wide in WP-3 Phase 3.
public sealed record GetBookingQueryResponse(
    Guid Id,
    Guid ResourceId,
    string ResourceName,
    Guid UserId,
    Guid? RecurrenceRuleId,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int Quantity,
    string? Title,
    BookingStatus Status,
    DateTime? CheckedInAtUtc,
    Guid? CancelledByUserId,
    DateTime? CancelledAtUtc,
    string? CancellationReason,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
