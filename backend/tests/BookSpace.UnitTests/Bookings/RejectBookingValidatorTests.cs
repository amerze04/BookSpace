using BookSpace.Application.Features.Bookings.RejectBooking;
using FluentValidation.TestHelper;

namespace BookSpace.UnitTests.Bookings;

public class RejectBookingValidatorTests
{
    private static readonly RejectBookingCommandRequestValidator Validator = new();

    [Fact]
    public void AWellFormedRequestPasses()
    {
        Validator.TestValidate(new RejectBookingCommandRequest(Guid.NewGuid(), "Room needed elsewhere"))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ANullNoteIsAccepted()
    {
        Validator.TestValidate(new RejectBookingCommandRequest(Guid.NewGuid()))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void BookingIdIsRequired()
    {
        Validator.TestValidate(new RejectBookingCommandRequest(Guid.Empty))
            .ShouldHaveValidationErrorFor(c => c.BookingId);
    }

    [Fact]
    public void NoteOverTheColumnLimitIsRejected()
    {
        var request = new RejectBookingCommandRequest(
            Guid.NewGuid(), new string('a', RejectBookingCommandRequestValidator.MaxNoteLength + 1));

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.Note);
    }
}
