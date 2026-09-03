using BookSpace.Domain.Availability;

namespace BookSpace.UnitTests.Availability;

// Merge is the step that turns Phase 3's deliberately adjacent windows and this
// schema's two-row overnight schedule into the continuous spans a member should
// see (WP-3 Phase 5).
public class IntervalAlgebraTests
{
    private static UtcInterval At(int startHour, int endHour) =>
        new(
            new DateTime(2026, 9, 7, startHour, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 7, endHour, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Merge_WithNoIntervals_ReturnsNothing()
    {
        Assert.Empty(IntervalAlgebra.Merge(Array.Empty<UtcInterval>()));
    }

    [Fact]
    public void Merge_LeavesSeparateIntervalsAloneAndOrdersThem()
    {
        var merged = IntervalAlgebra.Merge(new[] { At(14, 17), At(9, 12) });

        Assert.Equal(new[] { At(9, 12), At(14, 17) }, merged);
    }

    // The Phase 3 case: ClosesAt is exclusive, so 09:00-12:00 and 12:00-17:00
    // are two legal windows describing one continuous span.
    [Fact]
    public void Merge_JoinsTouchingIntervals()
    {
        var merged = IntervalAlgebra.Merge(new[] { At(9, 12), At(12, 17) });

        Assert.Equal(At(9, 17), Assert.Single(merged));
    }

    [Fact]
    public void Merge_JoinsOverlappingIntervals()
    {
        var merged = IntervalAlgebra.Merge(new[] { At(9, 13), At(11, 17) });

        Assert.Equal(At(9, 17), Assert.Single(merged));
    }

    // The case a single pass looks like it would get wrong: a long interval
    // swallowing shorter ones must not be shortened by them.
    [Fact]
    public void Merge_KeepsTheOuterBoundsWhenAnIntervalIsNested()
    {
        var merged = IntervalAlgebra.Merge(new[] { At(9, 18), At(10, 11), At(12, 13) });

        Assert.Equal(At(9, 18), Assert.Single(merged));
    }

    [Fact]
    public void Merge_JoinsARunOfIntervalsInOnePass()
    {
        var merged = IntervalAlgebra.Merge(new[] { At(15, 16), At(9, 10), At(10, 11), At(11, 15) });

        Assert.Equal(At(9, 16), Assert.Single(merged));
    }

    [Fact]
    public void Merge_CollapsesDuplicates()
    {
        var merged = IntervalAlgebra.Merge(new[] { At(9, 12), At(9, 12) });

        Assert.Equal(At(9, 12), Assert.Single(merged));
    }

    [Fact]
    public void Merge_KeepsAGapOfEvenOneSecond()
    {
        var first = At(9, 12);
        var second = new UtcInterval(first.EndUtc.AddSeconds(1), At(12, 17).EndUtc);

        var merged = IntervalAlgebra.Merge(new[] { first, second });

        Assert.Equal(new[] { first, second }, merged);
    }

    // ---- Subtract: how a blackout overrides the weekly schedule (FR-3.4) ----

    [Fact]
    public void Subtract_WithNothingToRemove_ReturnsTheSourceMerged()
    {
        var remaining = IntervalAlgebra.Subtract(
            new[] { At(12, 17), At(9, 12) }, Array.Empty<UtcInterval>());

        Assert.Equal(At(9, 17), Assert.Single(remaining));
    }

    [Fact]
    public void Subtract_FromNothing_ReturnsNothing()
    {
        Assert.Empty(IntervalAlgebra.Subtract(Array.Empty<UtcInterval>(), new[] { At(9, 17) }));
    }

    // The interesting shape: a blackout in the middle of an open day leaves two
    // spans, not a shortened one.
    [Fact]
    public void Subtract_ACutInTheMiddleSplitsTheIntervalInTwo()
    {
        var remaining = IntervalAlgebra.Subtract(new[] { At(9, 17) }, new[] { At(12, 13) });

        Assert.Equal(new[] { At(9, 12), At(13, 17) }, remaining);
    }

    [Fact]
    public void Subtract_ACutOverTheStartTrimsTheFront()
    {
        var remaining = IntervalAlgebra.Subtract(new[] { At(9, 17) }, new[] { At(8, 11) });

        Assert.Equal(At(11, 17), Assert.Single(remaining));
    }

    [Fact]
    public void Subtract_ACutOverTheEndTrimsTheBack()
    {
        var remaining = IntervalAlgebra.Subtract(new[] { At(9, 17) }, new[] { At(15, 20) });

        Assert.Equal(At(9, 15), Assert.Single(remaining));
    }

    [Fact]
    public void Subtract_ACutCoveringEverythingLeavesNothing()
    {
        Assert.Empty(IntervalAlgebra.Subtract(new[] { At(9, 17) }, new[] { At(0, 23) }));
    }

    // Touching is not overlapping, both ways round: a blackout that ends exactly
    // when the resource opens removes nothing.
    [Fact]
    public void Subtract_ACutThatMerelyTouchesRemovesNothing()
    {
        var remaining = IntervalAlgebra.Subtract(new[] { At(9, 17) }, new[] { At(7, 9), At(17, 19) });

        Assert.Equal(At(9, 17), Assert.Single(remaining));
    }

    [Fact]
    public void Subtract_SeveralCutsLeaveSeveralIntervals()
    {
        var remaining = IntervalAlgebra.Subtract(
            new[] { At(9, 18) }, new[] { At(11, 12), At(14, 15) });

        Assert.Equal(new[] { At(9, 11), At(12, 14), At(15, 18) }, remaining);
    }

    // Overlapping blackouts on one resource are explicitly allowed (decision
    // 0019), because the union of two blackouts is still blacked out. Merging
    // both sides first is what makes that need no special handling here.
    [Fact]
    public void Subtract_OverlappingCutsBehaveAsTheirUnion()
    {
        var remaining = IntervalAlgebra.Subtract(
            new[] { At(9, 18) }, new[] { At(11, 14), At(12, 16) });

        Assert.Equal(new[] { At(9, 11), At(16, 18) }, remaining);
    }

    [Fact]
    public void Subtract_ACutSpanningAGapAffectsBothSides()
    {
        var remaining = IntervalAlgebra.Subtract(
            new[] { At(9, 12), At(14, 18) }, new[] { At(11, 15) });

        Assert.Equal(new[] { At(9, 11), At(15, 18) }, remaining);
    }

    [Fact]
    public void Subtract_ACutFallingEntirelyInAGapChangesNothing()
    {
        var remaining = IntervalAlgebra.Subtract(
            new[] { At(9, 12), At(14, 18) }, new[] { At(12, 14) });

        Assert.Equal(new[] { At(9, 12), At(14, 18) }, remaining);
    }
}
