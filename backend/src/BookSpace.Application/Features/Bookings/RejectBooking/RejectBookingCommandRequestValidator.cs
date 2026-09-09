using FluentValidation;

namespace BookSpace.Application.Features.Bookings.RejectBooking;

// Shape only, mirroring ApproveBookingCommandRequestValidator.
public sealed class RejectBookingCommandRequestValidator : AbstractValidator<RejectBookingCommandRequest>
{
    // Matches ApprovalRequests.Note NVARCHAR(500).
    public const int MaxNoteLength = 500;

    public RejectBookingCommandRequestValidator()
    {
        RuleFor(c => c.BookingId)
            .NotEmpty()
            .WithMessage("BookingId is required.");

        RuleFor(c => c.Note)
            .MaximumLength(MaxNoteLength)
            .When(c => c.Note is not null);
    }
}
