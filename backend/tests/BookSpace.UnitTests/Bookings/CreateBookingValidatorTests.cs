using BookSpace.Application.Features.Bookings.CreateBooking;
using FluentValidation.TestHelper;

namespace BookSpace.UnitTests.Bookings;

// Shape only. Everything needing the resource, the clock or the calendar is a
// handler rule with a reason code — see CreateBookingCommandRequestHandlerTests.
//
// The instant rules are the ones worth having tests for: a fractional second or
// a zoneless timestamp both look harmless and both go wrong silently, one by
// rounding on write and one by being interpreted in the server's zone.
public class CreateBookingValidatorTests
{
    private static readonly CreateBookingCommandRequestValidator Validator = new();

    private static readonly DateTime Start = new(2027, 3, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2027, 3, 11, 10, 0, 0, DateTimeKind.Utc);

    private static CreateBookingCommandRequest Valid() =>
        new(Guid.NewGuid(), Start, End, 1, "Design review");

    [Fact]
    public void AWellFormedRequestPasses()
    {
        Validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ResourceIdIsRequired()
    {
        var request = Valid() with { ResourceId = Guid.Empty };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.ResourceId);
    }

    // CK_Bookings_Interval, restated as a field error so it is a 400 rather than
    // a 500 out of the Booking constructor.
    [Fact]
    public void EndMustBeAfterStart()
    {
        var request = Valid() with { EndsAtUtc = Start };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.EndsAtUtc);
    }

    // datetime2(0) *rounds* on write (CLAUDE.md §4.3), and here the stakes are
    // higher than elsewhere: these bounds are compared against other bookings'
    // bounds under a lock, so half a second of drift is the difference between an
    // overlap and an adjacency.
    [Fact]
    public void FractionalSecondsAreRefusedOnTheStart()
    {
        var request = Valid() with { StartsAtUtc = Start.AddMilliseconds(500) };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.StartsAtUtc);
    }

    [Fact]
    public void FractionalSecondsAreRefusedOnTheEnd()
    {
        var request = Valid() with { EndsAtUtc = End.AddTicks(1) };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.EndsAtUtc);
    }

    // An instant with no zone means nothing. System.Text.Json produces
    // Unspecified for a bare local-looking timestamp, and a handler could only
    // interpret it by guessing the server's zone — the booking would silently
    // cover the wrong hours.
    [Fact]
    public void AZonelessInstantIsRefused()
    {
        var request = Valid() with
        {
            StartsAtUtc = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified),
        };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.StartsAtUtc);
    }

    // An explicit offset is fine: the handler normalizes it with
    // ToUniversalTime, which is a conversion rather than a guess.
    [Fact]
    public void AnInstantWithAnExplicitOffsetIsAccepted()
    {
        var request = Valid() with
        {
            StartsAtUtc = DateTime.SpecifyKind(Start, DateTimeKind.Local),
        };

        Validator.TestValidate(request).ShouldNotHaveValidationErrorFor(c => c.StartsAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QuantityMustBePositive(int quantity)
    {
        var request = Valid() with { Quantity = quantity };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.Quantity);
    }

    // No upper bound: what is too many depends on the resource's Capacity, which
    // the validator cannot see. The procedure refuses it with CapacityExceeded, a
    // 409 that knows how much room there was.
    [Fact]
    public void AnImplausiblyLargeQuantityIsNotAShapeError()
    {
        var request = Valid() with { Quantity = 10_000 };

        Validator.TestValidate(request).ShouldNotHaveValidationErrorFor(c => c.Quantity);
    }

    [Fact]
    public void TitleIsOptional()
    {
        var request = Valid() with { Title = null };

        Validator.TestValidate(request).ShouldNotHaveValidationErrorFor(c => c.Title);
    }

    [Fact]
    public void TitleIsCappedAtTheColumnLength()
    {
        var request = Valid() with
        {
            Title = new string('x', CreateBookingCommandRequestValidator.MaxTitleLength + 1),
        };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.Title);
    }
}
