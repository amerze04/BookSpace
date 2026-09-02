using BookSpace.Application.Features.Resources.ReplaceApprovers;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 3 step 2, FR-3.3. Shape only — whether a user may actually approve
// is a 422 from the handler, and must be, or the refusal would arrive as a 400
// naming a field instead of the deliberately vague ApproverNotEligible.
public class ReplaceApproversValidatorTests
{
    private static readonly ReplaceApproversCommandRequestValidator Validator = new();

    private static ReplaceApproversCommandRequest Request(params Guid[] approverUserIds) =>
        new(Guid.NewGuid(), approverUserIds);

    [Fact]
    public void Accepts_AListOfDistinctIds()
    {
        var result = Validator.Validate(Request(Guid.NewGuid(), Guid.NewGuid()));

        Assert.True(result.IsValid);
    }

    // Legitimate: a resource that does not require approval is allowed to have no
    // approvers. It is refused only when RequiresApproval is set, and then by the
    // handler as a rule, not here as a shape problem.
    [Fact]
    public void Accepts_AnEmptyList()
    {
        var result = Validator.Validate(Request());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Rejects_AMissingList()
    {
        var result = Validator.Validate(new ReplaceApproversCommandRequest(Guid.NewGuid(), null!));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(ReplaceApproversCommandRequest.ApproverUserIds));
    }

    [Fact]
    public void Rejects_AnEmptyResourceId()
    {
        var result = Validator.Validate(
            new ReplaceApproversCommandRequest(Guid.Empty, new[] { Guid.NewGuid() }));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(ReplaceApproversCommandRequest.ResourceId));
    }

    [Fact]
    public void Rejects_AnEmptyGuidInTheList()
    {
        var result = Validator.Validate(Request(Guid.NewGuid(), Guid.Empty));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "ApproverUserIds[1]");
    }

    // Rejected rather than collapsed: the domain applies set semantics, so a
    // repeated id would store cleanly and the response would come back with fewer
    // entries than the request — quietly returning something other than what was
    // sent, which is the behaviour docs/decisions/0015 avoids elsewhere by
    // rejecting an oversized pageSize instead of clamping it.
    [Fact]
    public void Rejects_DuplicateIds()
    {
        var duplicated = Guid.NewGuid();

        var result = Validator.Validate(Request(duplicated, Guid.NewGuid(), duplicated));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(ReplaceApproversCommandRequest.ApproverUserIds)
                 && e.ErrorMessage.Contains("duplicates", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_MoreApproversThanTheCap()
    {
        var ids = Enumerable
            .Range(0, ReplaceApproversCommandRequestValidator.MaxApproversPerRequest + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        var result = Validator.Validate(Request(ids));

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            e => e.PropertyName == nameof(ReplaceApproversCommandRequest.ApproverUserIds)
                 && e.ErrorMessage.Contains("more than", StringComparison.Ordinal));
    }
}
