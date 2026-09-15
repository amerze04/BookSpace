using BookSpace.Application.Features.RecurrenceRules.CreateSeries;
using BookSpace.Domain.Enums;
using FluentValidation.TestHelper;

namespace BookSpace.UnitTests.RecurrenceRules;

// Shape only. Everything needing the resource, the clock, the calendar or the
// resource's own timezone is a handler rule with a reason code — see
// CreateRecurrenceSeriesCommandRequestHandlerTests.
public class CreateRecurrenceSeriesValidatorTests
{
    private static readonly CreateRecurrenceSeriesCommandRequestValidator Validator = new();

    private static readonly DateOnly StartDate = new(2027, 3, 8);

    private static CreateRecurrenceSeriesCommandRequest Valid() =>
        new(
            Guid.NewGuid(),
            RecurrenceFrequency.Weekly,
            IntervalValue: 1,
            new TimeOnly(9, 0),
            new TimeOnly(9, 30),
            StartDate,
            EndDate: StartDate.AddYears(1),
            OccurrenceCount: null,
            Quantity: 1,
            Title: "Standup");

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IntervalValueMustBePositive(int intervalValue)
    {
        var request = Valid() with { IntervalValue = intervalValue };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.IntervalValue);
    }

    // Not backed by a DB constraint (RecurrenceRule.cs explains why) — this is
    // the only place it is a 400 rather than a 500 from the constructor.
    [Theory]
    [InlineData(9, 0, 9, 0)]  // equal
    [InlineData(9, 30, 9, 0)] // end before start
    public void LocalEndTimeMustBeAfterLocalStartTime(int startHour, int startMinute, int endHour, int endMinute)
    {
        var request = Valid() with
        {
            LocalStartTime = new TimeOnly(startHour, startMinute),
            LocalEndTime = new TimeOnly(endHour, endMinute),
        };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.LocalEndTime);
    }

    [Fact]
    public void BothEndDateAndOccurrenceCountIsRejected()
    {
        var request = Valid() with { OccurrenceCount = 10 };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.EndDate);
    }

    [Fact]
    public void NeitherEndDateNorOccurrenceCountIsRejected()
    {
        var request = Valid() with { EndDate = null, OccurrenceCount = null };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.EndDate);
    }

    [Fact]
    public void OccurrenceCountAloneIsAccepted()
    {
        var request = Valid() with { EndDate = null, OccurrenceCount = 10 };

        Validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OccurrenceCountMustBePositiveWhenSet(int occurrenceCount)
    {
        var request = Valid() with { EndDate = null, OccurrenceCount = occurrenceCount };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.OccurrenceCount);
    }

    // Bug fix (item 12): this combination used to be rejected outright by a
    // flat IntervalValue <= 366 ceiling, despite its implied span (400 days)
    // being comfortably inside decision 0007's real two-year cap — a
    // perfectly legal series with no business reason to refuse it.
    [Fact]
    public void ADailyIntervalOverTheOldFlatCeilingIsAcceptedWhenTheImpliedSpanIsWithinTwoYears()
    {
        var request = Valid() with
        {
            Frequency = RecurrenceFrequency.Daily,
            IntervalValue = 400,
            EndDate = null,
            OccurrenceCount = 2,
        };

        Validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    // The real invariant this validator restates: two occurrences 800 days
    // apart (Daily, interval 800) implies a span well past decision 0007's
    // two-year cap, regardless of how small OccurrenceCount is.
    [Fact]
    public void ADailyIntervalWhoseImpliedSpanExceedsTwoYearsIsRejected()
    {
        var request = Valid() with
        {
            Frequency = RecurrenceFrequency.Daily,
            IntervalValue = 800,
            EndDate = null,
            OccurrenceCount = 2,
        };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.OccurrenceCount);
    }

    // A single occurrence has no second date to imply a span at all — the
    // check must not reject a large IntervalValue that is never actually
    // used for stepping.
    [Fact]
    public void ASingleOccurrenceAcceptsAnyIntervalValue()
    {
        var request = Valid() with
        {
            Frequency = RecurrenceFrequency.Daily,
            IntervalValue = 100_000,
            EndDate = null,
            OccurrenceCount = 1,
        };

        Validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EndDateMustNotBeBeforeStartDate()
    {
        var request = Valid() with { EndDate = StartDate.AddDays(-1) };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.EndDate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void QuantityMustBePositive(int quantity)
    {
        var request = Valid() with { Quantity = quantity };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.Quantity);
    }

    [Fact]
    public void TitleOverTheColumnLimitIsRejected()
    {
        var request = Valid() with { Title = new string('a', CreateRecurrenceSeriesCommandRequestValidator.MaxTitleLength + 1) };

        Validator.TestValidate(request).ShouldHaveValidationErrorFor(c => c.Title);
    }

    [Fact]
    public void ANullTitleIsAccepted()
    {
        var request = Valid() with { Title = null };

        Validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }
}
