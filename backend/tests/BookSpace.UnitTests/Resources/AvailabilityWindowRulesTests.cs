using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests.Resources;

// WP-3 Phase 3, FR-3.2. The one availability rule that needs the whole set:
// overlapping windows on a weekday are rejected, not unioned
// (docs/wp3-plan.md's "smaller calls").
public class AvailabilityWindowRulesTests
{
    private static AvailabilityWindowDefinition Window(DayOfWeek weekday, int opensHour, int closesHour) =>
        new(Guid.NewGuid(), weekday, new TimeOnly(opensHour, 0), new TimeOnly(closesHour, 0));

    [Fact]
    public void EnsureNoOverlaps_AcceptsAnEmptySchedule()
    {
        AvailabilityWindowRules.EnsureNoOverlaps(Array.Empty<AvailabilityWindowDefinition>());
    }

    [Fact]
    public void EnsureNoOverlaps_AcceptsDisjointWindowsOnOneWeekday()
    {
        AvailabilityWindowRules.EnsureNoOverlaps(new[]
        {
            Window(DayOfWeek.Monday, 9, 12),
            Window(DayOfWeek.Monday, 13, 17),
        });
    }

    // The owner's call: ClosesAt is exclusive, so a window ending at 12:00 and
    // one starting at 12:00 are two legal windows rather than a collision.
    [Fact]
    public void EnsureNoOverlaps_AcceptsAdjacentWindows()
    {
        AvailabilityWindowRules.EnsureNoOverlaps(new[]
        {
            Window(DayOfWeek.Monday, 9, 12),
            Window(DayOfWeek.Monday, 12, 17),
        });
    }

    // Same hours, different days, is the ordinary weekly schedule — the rule is
    // per-weekday and must not compare across days.
    [Fact]
    public void EnsureNoOverlaps_AcceptsIdenticalHoursOnDifferentWeekdays()
    {
        AvailabilityWindowRules.EnsureNoOverlaps(new[]
        {
            Window(DayOfWeek.Monday, 9, 17),
            Window(DayOfWeek.Tuesday, 9, 17),
            Window(DayOfWeek.Wednesday, 9, 17),
        });
    }

    [Theory]
    // Partial overlap, in each direction — order of submission must not matter.
    [InlineData(9, 13, 12, 17)]
    [InlineData(12, 17, 9, 13)]
    // Identical windows.
    [InlineData(9, 17, 9, 17)]
    // One strictly inside the other, which is the case an adjacent-pair scan
    // looks like it would miss.
    [InlineData(9, 17, 10, 11)]
    [InlineData(10, 11, 9, 17)]
    public void EnsureNoOverlaps_RejectsOverlappingWindowsOnTheSameWeekday(
        int firstOpens,
        int firstCloses,
        int secondOpens,
        int secondCloses)
    {
        var exception = Assert.Throws<OverlappingAvailabilityWindowException>(() =>
            AvailabilityWindowRules.EnsureNoOverlaps(new[]
            {
                Window(DayOfWeek.Monday, firstOpens, firstCloses),
                Window(DayOfWeek.Monday, secondOpens, secondCloses),
            }));

        Assert.Equal(ReasonCodes.OverlappingAvailabilityWindow, exception.ReasonCode);
        Assert.Equal(ErrorKind.Conflict, exception.Kind);
    }

    // A long window swallowing several short ones: the scan compares each window
    // only against its immediate predecessor once sorted, and this is the shape
    // that would expose the flaw if that reasoning were wrong.
    [Fact]
    public void EnsureNoOverlaps_RejectsAWindowContainingSeveralOthers()
    {
        Assert.Throws<OverlappingAvailabilityWindowException>(() =>
            AvailabilityWindowRules.EnsureNoOverlaps(new[]
            {
                Window(DayOfWeek.Monday, 8, 20),
                Window(DayOfWeek.Monday, 10, 11),
                Window(DayOfWeek.Monday, 14, 15),
            }));
    }

    // The overlap is on Wednesday, buried behind two clean days — proves the
    // grouping actually visits every weekday rather than stopping at the first.
    [Fact]
    public void EnsureNoOverlaps_FindsAnOverlapOnALaterWeekday()
    {
        Assert.Throws<OverlappingAvailabilityWindowException>(() =>
            AvailabilityWindowRules.EnsureNoOverlaps(new[]
            {
                Window(DayOfWeek.Monday, 9, 17),
                Window(DayOfWeek.Tuesday, 9, 17),
                Window(DayOfWeek.Wednesday, 9, 13),
                Window(DayOfWeek.Wednesday, 12, 17),
            }));
    }
}
