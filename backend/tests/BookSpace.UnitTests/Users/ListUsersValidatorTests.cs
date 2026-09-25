using BookSpace.Application.Features.Users;
using BookSpace.Application.Features.Users.ListUsers;

namespace BookSpace.UnitTests.Users;

// The scope parameter is the whole of user management phase 4, and the rule
// that matters is its *default*: omitted means the decision `0018`
// eligible-approver set, so the approvers picker keeps working without knowing
// this parameter exists. A change to that default is a silent change to what the
// picker offers, which is why it is pinned here rather than left to the
// integration suite alone.
public class ListUsersValidatorTests
{
    private readonly ListUsersQueryRequestValidator _validator = new();

    // docs/user-management-plan.md §4.6's whole argument in one assertion: a
    // forgotten parameter narrows rather than widens.
    [Fact]
    public void TheDefaultScopeIsTheEligibleApproverSet()
    {
        Assert.Equal(UserScope.EligibleApprovers, new ListUsersQueryRequest().Scope);
    }

    [Theory]
    [InlineData(UserScope.EligibleApprovers)]
    [InlineData(UserScope.All)]
    public void BothKnownScopesAreValid(UserScope scope)
    {
        Assert.True(_validator.Validate(new ListUsersQueryRequest(Scope: scope)).IsValid);
    }

    // Bound by name from the query string, so an unparseable word never reaches
    // the validator — model binding refuses it first. The numeric form is what
    // this catches: Enum.TryParse accepts any integer, so `?scope=99` would bind
    // to an undefined value and, without this rule, fall through to whichever
    // branch the repository's switch happened to pick.
    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void AnUndefinedScopeIsInvalid(int scope)
    {
        var result = _validator.Validate(new ListUsersQueryRequest(Scope: (UserScope)scope));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ListUsersQueryRequest.Scope));
    }

    [Fact]
    public void AnUndefinedScopeErrorNamesTheAllowedValues()
    {
        var result = _validator.Validate(new ListUsersQueryRequest(Scope: (UserScope)99));

        var message = Assert.Single(result.Errors).ErrorMessage;
        Assert.Contains(nameof(UserScope.EligibleApprovers), message, StringComparison.Ordinal);
        Assert.Contains(nameof(UserScope.All), message, StringComparison.Ordinal);
    }

    // Everything else about this query is unchanged by phase 4, and these are
    // here so a later edit to the validator cannot quietly drop them.
    [Fact]
    public void ADefaultRequestIsValid()
    {
        Assert.True(_validator.Validate(new ListUsersQueryRequest()).IsValid);
    }

    [Theory]
    [InlineData("fullName")]
    [InlineData("email")]
    [InlineData("-fullName")]
    public void AWhitelistedSortIsValid(string sort)
    {
        Assert.True(_validator.Validate(new ListUsersQueryRequest(Sort: sort)).IsValid);
    }

    // `roles` is deliberately absent from the whitelist — ordering by an owned
    // collection is ordering by "the first child row in whatever order SQL
    // Server returned it". See UserSortFields.
    [Theory]
    [InlineData("roles")]
    [InlineData("isActive")]
    [InlineData("password")]
    public void ASortFieldOutsideTheWhitelistIsInvalid(string sort)
    {
        Assert.False(_validator.Validate(new ListUsersQueryRequest(Sort: sort)).IsValid);
    }

    // The bound is Users.Email's column width, the wider of the two searched
    // columns — a longer term cannot match anything.
    [Fact]
    public void ASearchTermBeyondTheColumnWidthIsInvalid()
    {
        Assert.False(_validator.Validate(new ListUsersQueryRequest(Search: new string('s', 321))).IsValid);
    }

    [Fact]
    public void PagingFailuresAreRefusedRatherThanClamped()
    {
        Assert.False(_validator.Validate(new ListUsersQueryRequest(Page: 0)).IsValid);
        Assert.False(_validator.Validate(new ListUsersQueryRequest(PageSize: 0)).IsValid);
        Assert.False(_validator.Validate(new ListUsersQueryRequest(PageSize: 101)).IsValid);
    }
}
