using BookSpace.Application.Common.Pagination;
using FluentValidation;

namespace BookSpace.UnitTests.Pagination;

public class PagedQueryRulesTests
{
    private static readonly string[] Sortable = ["name", "createdAt"];

    private sealed record ProbeQuery(
        int Page = PagingDefaults.Page,
        int PageSize = PagingDefaults.PageSize,
        string? Sort = null) : IPagedQuery;

    private sealed class ProbeQueryValidator : AbstractValidator<ProbeQuery>
    {
        public ProbeQueryValidator() => this.AddPagingRules(Sortable);
    }

    private static readonly ProbeQueryValidator Validator = new();

    // The defaults a client gets by omitting the query string entirely.
    [Fact]
    public void DefaultQuery_IsValid()
    {
        Assert.True(Validator.Validate(new ProbeQuery()).IsValid);
        Assert.Equal(1, new ProbeQuery().Page);
        Assert.Equal(20, new ProbeQuery().PageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Page_BelowOne_IsRejected(int page)
    {
        var result = Validator.Validate(new ProbeQuery(Page: page));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ProbeQuery.Page));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(PagingDefaults.MaxPageSize + 1)]
    [InlineData(10_000)]
    public void PageSize_OutsideOneToMax_IsRejected(int pageSize)
    {
        var result = Validator.Validate(new ProbeQuery(PageSize: pageSize));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(ProbeQuery.PageSize));
    }

    [Fact]
    public void PageSize_AtTheCeiling_IsAccepted()
    {
        Assert.True(Validator.Validate(new ProbeQuery(PageSize: PagingDefaults.MaxPageSize)).IsValid);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("-createdAt")]
    [InlineData(null)]
    public void Sort_OnTheWhitelist_IsAccepted(string? sort)
    {
        Assert.True(Validator.Validate(new ProbeQuery(Sort: sort)).IsValid);
    }

    [Fact]
    public void Sort_OffTheWhitelist_IsRejectedAndNamesTheAllowedFields()
    {
        var result = Validator.Validate(new ProbeQuery(Sort: "capacity"));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.PropertyName == nameof(ProbeQuery.Sort));
        Assert.Contains("name", error.ErrorMessage);
        Assert.Contains("createdAt", error.ErrorMessage);
    }
}
