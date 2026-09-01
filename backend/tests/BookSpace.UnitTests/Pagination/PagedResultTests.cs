using BookSpace.Application.Common.Pagination;

namespace BookSpace.UnitTests.Pagination;

public class PagedResultTests
{
    [Theory]
    [InlineData(0, 20, 0)]    // empty result is zero pages, not one empty page
    [InlineData(1, 20, 1)]
    [InlineData(20, 20, 1)]   // exact multiple
    [InlineData(21, 20, 2)]   // remainder rounds up
    [InlineData(47, 20, 3)]
    public void TotalPages_RoundsUp(int totalCount, int pageSize, int expected)
    {
        var result = new PagedResult<string>([], Page: 1, PageSize: pageSize, TotalCount: totalCount);

        Assert.Equal(expected, result.TotalPages);
    }

    [Theory]
    [InlineData(1, 47, false, true)]   // first of three
    [InlineData(2, 47, true, true)]    // middle
    [InlineData(3, 47, true, false)]   // last
    [InlineData(1, 0, false, false)]   // empty: nowhere to go in either direction
    public void HasPreviousAndNextPage_ReflectPosition(
        int page, int totalCount, bool expectedPrevious, bool expectedNext)
    {
        var result = new PagedResult<string>([], page, PageSize: 20, TotalCount: totalCount);

        Assert.Equal(expectedPrevious, result.HasPreviousPage);
        Assert.Equal(expectedNext, result.HasNextPage);
    }

    // A page past the end is a legitimate request (the data shrank, or the
    // client guessed): it reports the real total and offers a way back, rather
    // than claiming another page exists.
    [Fact]
    public void PageBeyondTheEnd_ReportsNoNextPage()
    {
        var result = new PagedResult<string>([], Page: 9, PageSize: 20, TotalCount: 47);

        Assert.False(result.HasNextPage);
        Assert.True(result.HasPreviousPage);
        Assert.Equal(3, result.TotalPages);
    }

    [Fact]
    public void Empty_CarriesThePagingItWasAskedFor()
    {
        var result = PagedResult<string>.Empty(page: 2, pageSize: 50);

        Assert.Empty(result.Items);
        Assert.Equal(2, result.Page);
        Assert.Equal(50, result.PageSize);
        Assert.Equal(0, result.TotalCount);
    }
}
