using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

public class AvailabilityWindowTests
{
    [Fact]
    public void Constructor_SetsProperties_WhenClosesAtIsAfterOpensAt()
    {
        var id = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var opens = new TimeOnly(9, 0);
        var closes = new TimeOnly(17, 0);

        var window = new AvailabilityWindow(id, resourceId, DayOfWeek.Monday, opens, closes);

        Assert.Equal(id, window.Id);
        Assert.Equal(resourceId, window.ResourceId);
        Assert.Equal(DayOfWeek.Monday, window.Weekday);
        Assert.Equal(opens, window.OpensAt);
        Assert.Equal(closes, window.ClosesAt);
    }

    [Fact]
    public void Constructor_Throws_WhenClosesAtEqualsOpensAt()
    {
        var time = new TimeOnly(9, 0);

        Assert.Throws<ArgumentException>(() =>
            new AvailabilityWindow(Guid.NewGuid(), Guid.NewGuid(), DayOfWeek.Monday, time, time));
    }

    [Fact]
    public void Constructor_Throws_WhenClosesAtIsBeforeOpensAt()
    {
        Assert.Throws<ArgumentException>(() =>
            new AvailabilityWindow(Guid.NewGuid(), Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(17, 0), new TimeOnly(9, 0)));
    }
}
