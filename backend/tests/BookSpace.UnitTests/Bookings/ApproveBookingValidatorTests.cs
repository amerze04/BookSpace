using BookSpace.Application.Features.Bookings.ApproveBooking;
using FluentValidation.TestHelper;

namespace BookSpace.UnitTests.Bookings;

public class ApproveBookingValidatorTests
{
    private static readonly ApproveBookingCommandRequestValidator Validator = new();

    [Fact]
    public void AWellFormedRequestPasses()
    {
        Validator.TestValidate(new ApproveBookingCommandRequest(Guid.NewGuid(), "Looks fine"))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ANullNoteIsAccepted()
    {
        Validator.TestValidate(new ApproveBookingCommandRequest(Guid.NewGuid()))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void BookingIdIsRequired()
    {
        Validator.TestValidate(new ApproveBookingCommandRequest(Guid.Empty))
            .ShouldHaveValidationErrorFor(c => c.BookingId);
    }

    [Fact]
    public void NoteOverTheColumnLimitIsRejected()
    {
        var request = new ApproveBookingCommandRequest(
            Guid.NewGuid(), new string('a', ApproveBookingCommandRequestValidator.MaxNoteLength + 1));

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.Note);
    }
}
