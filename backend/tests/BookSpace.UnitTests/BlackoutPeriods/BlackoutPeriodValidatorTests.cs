using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.BlackoutPeriods;
using BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.DeleteBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;

namespace BookSpace.UnitTests.BlackoutPeriods;

// WP-3 Phase 4. Shape rules only — the clock-dependent rule (a blackout
// entirely in the past) is the handler's, and is covered by
// CreateBlackoutPeriodCommandRequestHandlerTests.
public class BlackoutPeriodValidatorTests
{
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly DateTime Starts = new(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

    private static readonly CreateBlackoutPeriodCommandRequestValidator CreateValidator = new();
    private static readonly UpdateBlackoutPeriodCommandRequestValidator UpdateValidator = new();
    private static readonly DeleteBlackoutPeriodCommandRequestValidator DeleteValidator = new();
    private static readonly ListBlackoutPeriodsQueryRequestValidator ListValidator = new();

    private static CreateBlackoutPeriodCommandRequest CreateRequest(
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null,
        string? reason = "Boiler service",
        Guid? resourceId = null) =>
        new(resourceId ?? ResourceId, startsAtUtc ?? Starts, endsAtUtc ?? Ends, reason);

    private static UpdateBlackoutPeriodCommandRequest UpdateRequest(
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null,
        string? reason = "Boiler service",
        Guid? resourceId = null,
        Guid? blackoutPeriodId = null) =>
        new(
            resourceId ?? ResourceId,
            blackoutPeriodId ?? Guid.NewGuid(),
            startsAtUtc ?? Starts,
            endsAtUtc ?? Ends,
            reason);

    // ---- Create ----

    [Fact]
    public void Create_AcceptsAWellFormedFutureInterval()
    {
        Assert.True(CreateValidator.Validate(CreateRequest()).IsValid);
    }

    // A blackout without a stated reason is legal: the column is nullable,
    // because "the room is unavailable" is sometimes all an admin can say.
    [Fact]
    public void Create_AcceptsAMissingReason()
    {
        Assert.True(CreateValidator.Validate(CreateRequest(reason: null)).IsValid);
    }

    [Fact]
    public void Create_RejectsAnEmptyResourceId()
    {
        var result = CreateValidator.Validate(CreateRequest(resourceId: Guid.Empty));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBlackoutPeriodCommandRequest.ResourceId));
    }

    // CK_BlackoutPeriods_Interval, surfaced as a 400 naming the field rather than
    // a 500 from the entity's ArgumentException.
    [Fact]
    public void Create_RejectsAnInvertedInterval()
    {
        var result = CreateValidator.Validate(CreateRequest(startsAtUtc: Ends, endsAtUtc: Starts));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBlackoutPeriodCommandRequest.EndsAtUtc));
    }

    [Fact]
    public void Create_RejectsAZeroWidthInterval()
    {
        Assert.False(CreateValidator.Validate(CreateRequest(startsAtUtc: Starts, endsAtUtc: Starts)).IsValid);
    }

    // Both columns are datetime2(0): a fractional value would be rounded on write
    // and the response would disagree with the row a client reads back
    // (CLAUDE.md §4.3). Rejected rather than truncated.
    [Fact]
    public void Create_RejectsFractionalSecondsOnStart()
    {
        var result = CreateValidator.Validate(CreateRequest(startsAtUtc: Starts.AddMilliseconds(500)));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBlackoutPeriodCommandRequest.StartsAtUtc));
    }

    [Fact]
    public void Create_RejectsFractionalSecondsOnEnd()
    {
        var result = CreateValidator.Validate(CreateRequest(endsAtUtc: Ends.AddMilliseconds(500)));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBlackoutPeriodCommandRequest.EndsAtUtc));
    }

    // An instant with no zone can only be interpreted by guessing one, and the
    // likeliest guess is the server's — the silent app/database disagreement
    // §4.3 exists to prevent.
    [Fact]
    public void Create_RejectsAnInstantWithNoZone()
    {
        var unspecified = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Unspecified);

        var result = CreateValidator.Validate(CreateRequest(startsAtUtc: unspecified));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBlackoutPeriodCommandRequest.StartsAtUtc));
    }

    // An explicit offset is accepted, and the handler converts it — being
    // explicit about a zone is exactly what the rule asks for.
    [Fact]
    public void Create_AcceptsALocalInstantBecauseItCarriesAZone()
    {
        Assert.True(CreateValidator.Validate(CreateRequest(startsAtUtc: Starts.ToLocalTime())).IsValid);
    }

    [Fact]
    public void Create_RejectsAnOverLongReason()
    {
        var tooLong = new string('x', BlackoutPeriodFieldRules.MaxReasonLength + 1);

        var result = CreateValidator.Validate(CreateRequest(reason: tooLong));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBlackoutPeriodCommandRequest.Reason));
    }

    [Fact]
    public void Create_AcceptsAReasonAtExactlyTheLimit()
    {
        var atLimit = new string('x', BlackoutPeriodFieldRules.MaxReasonLength);

        Assert.True(CreateValidator.Validate(CreateRequest(reason: atLimit)).IsValid);
    }

    // ---- Update ----

    // The point of the shared BlackoutPeriodFieldRules: an edit is a full
    // representation, so the two payloads have to be judged identically or a
    // value the create endpoint refuses could be smuggled in through the edit.
    // Asserted against the same cases rather than trusting the extension method.
    [Fact]
    public void Update_AcceptsAWellFormedFutureInterval()
    {
        Assert.True(UpdateValidator.Validate(UpdateRequest()).IsValid);
    }

    [Fact]
    public void Update_RejectsAnEmptyBlackoutPeriodId()
    {
        var result = UpdateValidator.Validate(UpdateRequest(blackoutPeriodId: Guid.Empty));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateBlackoutPeriodCommandRequest.BlackoutPeriodId));
    }

    [Fact]
    public void Update_RejectsAnInvertedInterval()
    {
        var result = UpdateValidator.Validate(UpdateRequest(startsAtUtc: Ends, endsAtUtc: Starts));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(UpdateBlackoutPeriodCommandRequest.EndsAtUtc));
    }

    [Fact]
    public void Update_RejectsFractionalSeconds()
    {
        Assert.False(UpdateValidator.Validate(UpdateRequest(startsAtUtc: Starts.AddMilliseconds(500))).IsValid);
    }

    [Fact]
    public void Update_RejectsAnInstantWithNoZone()
    {
        var unspecified = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Unspecified);

        Assert.False(UpdateValidator.Validate(UpdateRequest(startsAtUtc: unspecified)).IsValid);
    }

    [Fact]
    public void Update_RejectsAnOverLongReason()
    {
        var tooLong = new string('x', BlackoutPeriodFieldRules.MaxReasonLength + 1);

        Assert.False(UpdateValidator.Validate(UpdateRequest(reason: tooLong)).IsValid);
    }

    // ---- Delete ----

    [Fact]
    public void Delete_AcceptsTwoRealIds()
    {
        Assert.True(DeleteValidator.Validate(
            new DeleteBlackoutPeriodCommandRequest(ResourceId, Guid.NewGuid())).IsValid);
    }

    [Fact]
    public void Delete_RejectsEmptyIds()
    {
        var result = DeleteValidator.Validate(
            new DeleteBlackoutPeriodCommandRequest(Guid.Empty, Guid.Empty));

        Assert.Equal(2, result.Errors.Count);
    }

    // ---- List ----

    [Fact]
    public void List_AcceptsAnOmittedRange()
    {
        Assert.True(ListValidator.Validate(new ListBlackoutPeriodsQueryRequest(ResourceId)).IsValid);
    }

    [Fact]
    public void List_AcceptsAnOpenEndedRange()
    {
        Assert.True(ListValidator.Validate(new ListBlackoutPeriodsQueryRequest(ResourceId, From: Starts)).IsValid);
        Assert.True(ListValidator.Validate(new ListBlackoutPeriodsQueryRequest(ResourceId, To: Ends)).IsValid);
    }

    // An inverted range would return nothing, which reads as "this resource has
    // no blackouts" — a wrong answer rather than an error.
    [Fact]
    public void List_RejectsAnInvertedRange()
    {
        var result = ListValidator.Validate(
            new ListBlackoutPeriodsQueryRequest(ResourceId, From: Ends, To: Starts));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBlackoutPeriodsQueryRequest.To));
    }

    // Zero-width can overlap nothing, so it is never what the caller meant.
    [Fact]
    public void List_RejectsAZeroWidthRange()
    {
        Assert.False(ListValidator.Validate(
            new ListBlackoutPeriodsQueryRequest(ResourceId, From: Starts, To: Starts)).IsValid);
    }

    [Fact]
    public void List_RejectsARangeBoundWithNoZone()
    {
        var unspecified = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Unspecified);

        var result = ListValidator.Validate(new ListBlackoutPeriodsQueryRequest(ResourceId, From: unspecified));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBlackoutPeriodsQueryRequest.From));
    }

    // Paging comes from the shared rules, and the whitelist is this endpoint's
    // own — asserted here so a sort field renamed in one place fails loudly.
    [Fact]
    public void List_RejectsAnOversizedPageSize()
    {
        var result = ListValidator.Validate(
            new ListBlackoutPeriodsQueryRequest(ResourceId, PageSize: PagingDefaults.MaxPageSize + 1));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBlackoutPeriodsQueryRequest.PageSize));
    }

    [Theory]
    [InlineData("startsAtUtc")]
    [InlineData("-startsAtUtc")]
    [InlineData("endsAtUtc")]
    [InlineData("-endsAtUtc")]
    public void List_AcceptsEveryWhitelistedSortField(string sort)
    {
        Assert.True(ListValidator.Validate(new ListBlackoutPeriodsQueryRequest(ResourceId, Sort: sort)).IsValid);
    }

    [Fact]
    public void List_RejectsASortFieldThatIsNotWhitelisted()
    {
        var result = ListValidator.Validate(new ListBlackoutPeriodsQueryRequest(ResourceId, Sort: "reason"));

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListBlackoutPeriodsQueryRequest.Sort));
    }
}
