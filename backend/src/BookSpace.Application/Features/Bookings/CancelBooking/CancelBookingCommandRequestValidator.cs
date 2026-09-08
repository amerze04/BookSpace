using FluentValidation;

namespace BookSpace.Application.Features.Bookings.CancelBooking;

// Shape only. Whether *this* booking may be cancelled needs the booking and the
// clock, so it is a rule check in the handler carrying BookingNotCancellable —
// the same split every other write in this project uses.
//
// No ICurrentUser dependency here, unlike ListBookingsQueryRequestValidator:
// there is no admin-only *field* on this command. The permission question is
// "whose booking is this", which is answered by the owner filter in the query
// and can only ever be a 404, never a field error.
public sealed class CancelBookingCommandRequestValidator
    : AbstractValidator<CancelBookingCommandRequest>
{
    // Matches Bookings.CancellationReason NVARCHAR(300), restated so an
    // over-long reason is a 400 naming the field rather than a truncation or a
    // SQL error. The blackout cascade truncates instead, and the difference is
    // deliberate: that text is composed by the system from a blackout's own
    // reason, so there is no caller to tell.
    public const int MaxReasonLength = 300;

    public CancelBookingCommandRequestValidator()
    {
        // Guid.Empty is a malformed request, not a lookup that misses — the
        // route constraint `{id:guid}` catches everything else.
        RuleFor(c => c.BookingId)
            .NotEmpty()
            .WithMessage("BookingId is required.");

        // Optional: "the meeting is off" is often all there is to say, and the
        // column is nullable.
        RuleFor(c => c.Reason)
            .MaximumLength(MaxReasonLength)
            .When(c => c.Reason is not null);
    }
}
