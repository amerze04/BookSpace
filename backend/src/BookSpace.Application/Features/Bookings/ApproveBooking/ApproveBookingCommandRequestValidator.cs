using FluentValidation;

namespace BookSpace.Application.Features.Bookings.ApproveBooking;

// Shape only, mirroring CancelBookingCommandRequestValidator. Whether *this*
// caller may decide on *this* booking is a handler question (ApprovalReach,
// BookingNotFoundException); whether it is still Pending is dbo.ApproveBooking's
// own guard (BookingNotPendingException). Neither is knowable here.
public sealed class ApproveBookingCommandRequestValidator : AbstractValidator<ApproveBookingCommandRequest>
{
    // Matches ApprovalRequests.Note NVARCHAR(500).
    public const int MaxNoteLength = 500;

    public ApproveBookingCommandRequestValidator()
    {
        RuleFor(c => c.BookingId)
            .NotEmpty()
            .WithMessage("BookingId is required.");

        RuleFor(c => c.Note)
            .MaximumLength(MaxNoteLength)
            .When(c => c.Note is not null);
    }
}
