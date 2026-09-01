using BookSpace.Application.Features.Resources;
using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.UpdateResource;
using FluentValidation.Results;

namespace BookSpace.UnitTests.Resources;

// Shape validation only (ResourceFieldRules). The rules that need the host or
// another aggregate — InvalidTimeZone, ApproversRequired,
// CapacityBelowExistingBookings — are handler tests, and are not repeated here.
//
// Both validators are exercised against the same cases on purpose: the rules are
// shared through ResourceFieldRules, and a test that only covered create would
// not notice if the edit validator stopped calling it.
public class ResourceWriteValidatorTests
{
    private static readonly CreateResourceCommandRequestValidator CreateValidator = new();
    private static readonly UpdateResourceCommandRequestValidator UpdateValidator = new();

    private static ValidationResult ValidateCreate(
        string name = "Room A",
        string? description = null,
        string resourceType = "Room",
        int capacity = 4,
        string timeZoneId = "America/New_York",
        int? min = null,
        int? max = null) =>
        CreateValidator.Validate(new CreateResourceCommandRequest(
            name, description, resourceType, capacity, timeZoneId, false, min, max));

    private static ValidationResult ValidateUpdate(
        Guid? resourceId = null,
        string name = "Room A",
        string? description = null,
        string resourceType = "Room",
        int capacity = 4,
        string timeZoneId = "America/New_York",
        int? min = null,
        int? max = null) =>
        UpdateValidator.Validate(new UpdateResourceCommandRequest(
            resourceId ?? Guid.NewGuid(), name, description, resourceType, capacity, timeZoneId, false, min, max));

    private static void AssertFailsOn(ValidationResult result, string propertyName)
    {
        Assert.False(result.IsValid);
        Assert.Contains(propertyName, result.Errors.Select(e => e.PropertyName));
    }

    [Fact]
    public void BothValidators_AcceptAMinimalValidPayload()
    {
        Assert.True(ValidateCreate().IsValid);
        Assert.True(ValidateUpdate().IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_MustNotBeBlank(string name)
    {
        AssertFailsOn(ValidateCreate(name: name), nameof(CreateResourceCommandRequest.Name));
        AssertFailsOn(ValidateUpdate(name: name), nameof(UpdateResourceCommandRequest.Name));
    }

    // The column is NVARCHAR(200): without this, an oversized name would reach
    // SQL Server and come back as a 500 rather than a named field error.
    [Fact]
    public void Name_MustFitTheColumn()
    {
        var tooLong = new string('x', ResourceFieldRules.NameMaxLength + 1);

        AssertFailsOn(ValidateCreate(name: tooLong), nameof(CreateResourceCommandRequest.Name));
        AssertFailsOn(ValidateUpdate(name: tooLong), nameof(UpdateResourceCommandRequest.Name));
    }

    [Fact]
    public void Description_MustFitTheColumn()
    {
        var tooLong = new string('x', ResourceFieldRules.DescriptionMaxLength + 1);

        AssertFailsOn(ValidateCreate(description: tooLong), nameof(CreateResourceCommandRequest.Description));
        AssertFailsOn(ValidateUpdate(description: tooLong), nameof(UpdateResourceCommandRequest.Description));
    }

    // Null is "no description", which is not the same as an invalid one.
    [Fact]
    public void Description_MayBeNull()
    {
        Assert.True(ValidateCreate(description: null).IsValid);
        Assert.True(ValidateUpdate(description: null).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResourceType_MustNotBeBlank(string resourceType)
    {
        AssertFailsOn(ValidateCreate(resourceType: resourceType), nameof(CreateResourceCommandRequest.ResourceType));
        AssertFailsOn(ValidateUpdate(resourceType: resourceType), nameof(UpdateResourceCommandRequest.ResourceType));
    }

    [Fact]
    public void ResourceType_MustFitTheColumn()
    {
        var tooLong = new string('x', ResourceFieldRules.ResourceTypeMaxLength + 1);

        AssertFailsOn(ValidateCreate(resourceType: tooLong), nameof(CreateResourceCommandRequest.ResourceType));
        AssertFailsOn(ValidateUpdate(resourceType: tooLong), nameof(UpdateResourceCommandRequest.ResourceType));
    }

    // CK_Resources_Capacity, and decision 0005: zero concurrent units is a
    // resource nothing can ever be booked on.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Capacity_MustBePositive(int capacity)
    {
        AssertFailsOn(ValidateCreate(capacity: capacity), nameof(CreateResourceCommandRequest.Capacity));
        AssertFailsOn(ValidateUpdate(capacity: capacity), nameof(UpdateResourceCommandRequest.Capacity));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TimeZoneId_MustNotBeBlank(string timeZoneId)
    {
        AssertFailsOn(ValidateCreate(timeZoneId: timeZoneId), nameof(CreateResourceCommandRequest.TimeZoneId));
        AssertFailsOn(ValidateUpdate(timeZoneId: timeZoneId), nameof(UpdateResourceCommandRequest.TimeZoneId));
    }

    [Fact]
    public void TimeZoneId_MustFitTheColumn()
    {
        var tooLong = new string('x', ResourceFieldRules.TimeZoneIdMaxLength + 1);

        AssertFailsOn(ValidateCreate(timeZoneId: tooLong), nameof(CreateResourceCommandRequest.TimeZoneId));
        AssertFailsOn(ValidateUpdate(timeZoneId: tooLong), nameof(UpdateResourceCommandRequest.TimeZoneId));
    }

    // The validator's half of CK_Resources_DurationLimits — a named field error
    // instead of a constraint violation from SQL Server.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MinDuration_WhenSupplied_MustBePositive(int min)
    {
        AssertFailsOn(ValidateCreate(min: min), nameof(CreateResourceCommandRequest.MinDurationMinutes));
        AssertFailsOn(ValidateUpdate(min: min), nameof(UpdateResourceCommandRequest.MinDurationMinutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void MaxDuration_WhenSupplied_MustBePositive(int max)
    {
        AssertFailsOn(ValidateCreate(max: max), nameof(CreateResourceCommandRequest.MaxDurationMinutes));
        AssertFailsOn(ValidateUpdate(max: max), nameof(UpdateResourceCommandRequest.MaxDurationMinutes));
    }

    [Fact]
    public void MaxDuration_MustNotBeBelowMin()
    {
        AssertFailsOn(ValidateCreate(min: 120, max: 60), nameof(CreateResourceCommandRequest.MaxDurationMinutes));
        AssertFailsOn(ValidateUpdate(min: 120, max: 60), nameof(UpdateResourceCommandRequest.MaxDurationMinutes));
    }

    [Fact]
    public void MaxDuration_MayEqualMin()
    {
        Assert.True(ValidateCreate(min: 60, max: 60).IsValid);
        Assert.True(ValidateUpdate(min: 60, max: 60).IsValid);
    }

    // Null means "no limit" on either bound, so one supplied without the other
    // is a legitimate configuration and the cross-field rule must not fire.
    [Theory]
    [InlineData(30, null)]
    [InlineData(null, 240)]
    public void DurationBounds_AreIndependentlyOptional(int? min, int? max)
    {
        Assert.True(ValidateCreate(min: min, max: max).IsValid);
        Assert.True(ValidateUpdate(min: min, max: max).IsValid);
    }

    [Fact]
    public void UpdateValidator_RejectsAnEmptyResourceId()
    {
        AssertFailsOn(ValidateUpdate(resourceId: Guid.Empty), nameof(UpdateResourceCommandRequest.ResourceId));
    }
}
