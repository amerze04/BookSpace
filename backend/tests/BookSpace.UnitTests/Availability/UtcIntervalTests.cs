using BookSpace.Domain.Availability;

namespace BookSpace.UnitTests.Availability;

// UtcInterval is the unit the whole availability calculation works in (WP-3
// Phase 5), so its two guards matter more than they look: a DateTime with the
// wrong Kind is indistinguishable from a correct one by value, and an empty
// span would force every later step to ask whether an interval is real.
public class UtcIntervalTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 9, 7, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_KeepsBothInstantsAndReportsDuration()
    {
        var interval = new UtcInterval(Start, End);

        Assert.Equal(Start, interval.StartUtc);
        Assert.Equal(End, interval.EndUtc);
        Assert.Equal(TimeSpan.FromHours(8), interval.Duration);
    }

    // CLAUDE.md §4.3: datetime2 carries no offset, so an Unspecified DateTime
    // that was really local looks exactly like a correct UTC one. Refusing it
    // here is the cheapest place to notice.
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Constructor_Throws_WhenStartIsNotUtc(DateTimeKind kind)
    {
        Assert.Throws<ArgumentException>(() =>
            new UtcInterval(DateTime.SpecifyKind(Start, kind), End));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Constructor_Throws_WhenEndIsNotUtc(DateTimeKind kind)
    {
        Assert.Throws<ArgumentException>(() =>
            new UtcInterval(Start, DateTime.SpecifyKind(End, kind)));
    }

    // Emptiness is expressed by absence from a list, never by a zero-length
    // interval — see the type's header.
    [Fact]
    public void Constructor_Throws_WhenTheSpanIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => new UtcInterval(Start, Start));
    }

    [Fact]
    public void Constructor_Throws_WhenTheSpanIsInverted()
    {
        Assert.Throws<ArgumentException>(() => new UtcInterval(End, Start));
    }
}
