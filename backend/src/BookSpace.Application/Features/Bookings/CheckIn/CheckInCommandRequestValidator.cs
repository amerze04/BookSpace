using FluentValidation;

namespace BookSpace.Application.Features.Bookings.CheckIn;

// Shape only, matching CancelBookingCommandRequestValidator: whether *this*
// booking may be checked in needs the booking itself, so that is
// BookingNotCheckable in the handler.
public sealed class CheckInCommandRequestValidator : AbstractValidator<CheckInCommandRequest>
{
    public CheckInCommandRequestValidator()
    {
        RuleFor(c => c.BookingId)
            .NotEmpty()
            .WithMessage("BookingId is required.");
    }
}
