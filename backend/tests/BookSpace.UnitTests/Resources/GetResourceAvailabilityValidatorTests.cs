using BookSpace.Application.Features.Resources;
using BookSpace.Application.Features.Resources.GetResourceAvailability;

namespace BookSpace.UnitTests.Resources;

// Request shape for GET /resources/{id}/availability (WP-3 Phase 5). Everything
// here comes back as ValidationFailed 400 with a field error — the range cap
// included, which is why this phase adds no reason code at all
// (AvailabilityQueryRules).
public class GetResourceAvailabilityValidatorTests
{
    private readonly GetResourceAvailabilityQueryRequestValidator _validator = new();

    private static readonly DateOnly Monday = new(2026, 9, 7);

    private static GetResourceAvailabilityQueryRequest Query(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? resourceId = null) =>
        new(resourceId ?? Guid.NewGuid(), from ?? Monday, to ?? Monday.AddDays(6));

    [Fact]
    public void AWeekIsValid()
    {
        Assert.True(_validator.Validate(Query()).IsValid);
    }

    // The PRD's own flow is "selects a resource and date", so one day has to be a
    // legal range.
    [Fact]
    public void ASingleDayIsValid()
    {
        Assert.True(_validator.Validate(Query(Monday, Monday)).IsValid);
    }

    [Fact]
    public void ResourceIdIsRequired()
    {
        var result = _validator.Validate(Query(resourceId: Guid.Empty));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(GetResourceAvailabilityQueryRequest.ResourceId));
    }

    // An omitted query parameter binds to default(DateOnly) rather than failing to
    // bind, so without this the endpoint would answer about January 0001 and
    // return an empty list — which reads as "nothing is bookable".
    [Fact]
    public void FromIsRequired()
    {
        var result = _validator.Validate(Query(from: default(DateOnly)));

        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(GetResourceAvailabilityQueryRequest.FromLocalDate));
    }

    [Fact]
    public void ToIsRequired()
    {
        var result = _validator.Validate(Query(to: default(DateOnly)));

        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(GetResourceAvailabilityQueryRequest.ToLocalDate));
    }

    // Refused rather than answered with an empty list, which a client could not
    // tell from a resource that never opens.
    [Fact]
    public void AnInvertedRangeIsRefused()
    {
        var result = _validator.Validate(Query(Monday, Monday.AddDays(-1)));

        Assert.False(result.IsValid);
    }

    // The boundary, both sides of it. The range is inclusive of both ends, so 90
    // days is from a date to that date plus 89.
    [Fact]
    public void ExactlyNinetyDaysIsValid()
    {
        var result = _validator.Validate(
            Query(Monday, Monday.AddDays(AvailabilityQueryRules.MaxRangeDays - 1)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void NinetyOneDaysIsRefused()
    {
        var result = _validator.Validate(
            Query(Monday, Monday.AddDays(AvailabilityQueryRules.MaxRangeDays)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("90"));
    }

    // An inverted range must not also report the cap: the length of a backwards
    // range is negative, and two errors for one mistake is noise.
    [Fact]
    public void AnInvertedRangeIsNotAlsoReportedAsTooLong()
    {
        var result = _validator.Validate(Query(Monday, Monday.AddDays(-200)));

        Assert.DoesNotContain(result.Errors, e => e.ErrorMessage.Contains("90"));
    }
}
