using BookSpace.Application.Common.Pagination;

namespace BookSpace.UnitTests.Pagination;

public class SortOptionTests
{
    private static readonly string[] Allowed = ["name", "createdAt"];

    [Theory]
    [InlineData("name", "name", false)]
    [InlineData("-name", "name", true)]
    [InlineData("createdAt", "createdAt", false)]
    [InlineData("-createdAt", "createdAt", true)]
    [InlineData("  -name  ", "name", true)]     // trimmed
    [InlineData("NAME", "name", false)]         // case-insensitive match...
    [InlineData("-CreatedAt", "createdAt", true)]
    public void TryParse_AcceptsWhitelistedFields(string value, string expectedField, bool expectedDescending)
    {
        var parsed = SortOption.TryParse(value, Allowed, out var sort);

        Assert.True(parsed);
        // ...but the whitelist's spelling is what comes back, so a handler can
        // switch on the canonical name without normalizing again.
        Assert.Equal(expectedField, sort!.Field);
        Assert.Equal(expectedDescending, sort.Descending);
    }

    // Omitting the parameter is not an error — it means "the endpoint's own
    // default order", which each query decides for itself.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_TreatsMissingValueAsNoPreference(string? value)
    {
        var parsed = SortOption.TryParse(value, Allowed, out var sort);

        Assert.True(parsed);
        Assert.Null(sort);
    }

    [Theory]
    [InlineData("capacity")]                 // real column, not sortable here
    [InlineData("passwordHash")]             // never exposed
    [InlineData("-")]                        // descending marker with no field
    [InlineData("name; DROP TABLE Users")]
    public void TryParse_RejectsAnythingElse(string value)
    {
        var parsed = SortOption.TryParse(value, Allowed, out var sort);

        Assert.False(parsed);
        Assert.Null(sort);
    }
}
