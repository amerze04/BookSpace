using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests;

public class BookingTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Starts = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ends = new(2026, 8, 24, 10, 0, 0, DateTimeKind.Utc);

    private static Booking CreateValid(BookingStatus status = BookingStatus.Confirmed) =>
        new(Guid.NewGuid(), OrgId, ResourceId, UserId, null, Starts, Ends, 1, "Team sync", status, ActorId, NowUtc);

    [Fact]
    public void Constructor_Throws_WhenEndsAtUtcIsNotAfterStartsAtUtc()
    {
        Assert.Throws<ArgumentException>(() =>
            new Booking(Guid.NewGuid(), OrgId, ResourceId, UserId, null, Starts, Starts, 1, null, BookingStatus.Pending, ActorId, NowUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ThrowsOnNonPositiveQuantity(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Booking(Guid.NewGuid(), OrgId, ResourceId, UserId, null, Starts, Ends, quantity, null, BookingStatus.Pending, ActorId, NowUtc));
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public void Cancel_Throws_WhenAlreadyInATerminalStatus(BookingStatus status)
    {
        var booking = CreateValid(status);

        Assert.Throws<InvalidOperationException>(() => booking.Cancel(ActorId, "reason", NowUtc));
    }

    [Fact]
    public void Cancel_SetsCancellationFields()
    {
        var booking = CreateValid(BookingStatus.Confirmed);
        var actor = Guid.NewGuid();
        var later = NowUtc.AddMinutes(5);

        booking.Cancel(actor, "No longer needed", later);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(actor, booking.CancelledByUserId);
        Assert.Equal(later, booking.CancelledAtUtc);
        Assert.Equal("No longer needed", booking.CancellationReason);
        Assert.Equal(actor, booking.UpdatedByUserId);
    }

    [Fact]
    public void Cancel_AllowsNullActor_ForSystemInitiatedCancellation()
    {
        var booking = CreateValid(BookingStatus.Confirmed);

        booking.Cancel(null, "Blackout added", NowUtc.AddMinutes(1));

        Assert.Null(booking.CancelledByUserId);
    }

    [Fact]
    public void CheckIn_SetsCheckedInAtUtc_WhenConfirmed()
    {
        var booking = CreateValid(BookingStatus.Confirmed);
        var later = NowUtc.AddMinutes(1);

        booking.CheckIn(later);

        Assert.Equal(later, booking.CheckedInAtUtc);
        Assert.Equal(UserId, booking.UpdatedByUserId);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.NoShow)]
    public void CheckIn_Throws_WhenNotConfirmed(BookingStatus status)
    {
        var booking = CreateValid(status);

        Assert.Throws<InvalidOperationException>(() => booking.CheckIn(NowUtc));
    }

    [Fact]
    public void IsNoShow_ReturnsTrue_WhenConfirmedNotCheckedInAndPastGrace()
    {
        var booking = CreateValid(BookingStatus.Confirmed);
        var afterGrace = Starts.AddMinutes(20);

        Assert.True(booking.IsNoShow(afterGrace, graceMinutes: 15));
    }

    [Fact]
    public void IsNoShow_ReturnsFalse_WhenWithinGrace()
    {
        var booking = CreateValid(BookingStatus.Confirmed);
        var withinGrace = Starts.AddMinutes(10);

        Assert.False(booking.IsNoShow(withinGrace, graceMinutes: 15));
    }

    [Fact]
    public void IsNoShow_ReturnsFalse_WhenCheckedIn()
    {
        var booking = CreateValid(BookingStatus.Confirmed);
        booking.CheckIn(Starts.AddMinutes(5));

        Assert.False(booking.IsNoShow(Starts.AddMinutes(30), graceMinutes: 15));
    }

    [Fact]
    public void MarkNoShow_SetsStatusAndNullUpdatedByUserId()
    {
        var booking = CreateValid(BookingStatus.Confirmed);
        var later = Starts.AddMinutes(30);

        booking.MarkNoShow(later);

        Assert.Equal(BookingStatus.NoShow, booking.Status);
        Assert.Null(booking.UpdatedByUserId);
        Assert.Equal(later, booking.UpdatedAtUtc);
    }

    [Fact]
    public void MarkNoShow_Throws_WhenNotConfirmed()
    {
        var booking = CreateValid(BookingStatus.Pending);

        Assert.Throws<InvalidOperationException>(() => booking.MarkNoShow(NowUtc));
    }

    // ---- Decision 0001: the blackout cascade (WP-3 Phase 4) ----

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    public void CancelForBlackout_CancelsAClaimOnTheResource(BookingStatus status)
    {
        var booking = CreateValid(status);

        booking.CancelForBlackout("Blackout: boiler service", NowUtc);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal("Blackout: boiler service", booking.CancellationReason);
        Assert.Equal(NowUtc, booking.CancelledAtUtc);
    }

    // Decision 0001 records the acting admin on BlackoutPeriods.CreatedByUserId,
    // not here: writing them onto the booking would claim a person acted on this
    // booking, when what happened is that a rule did.
    [Fact]
    public void CancelForBlackout_LeavesNoActingUserOnTheBooking()
    {
        var booking = CreateValid();

        booking.CancelForBlackout("Blackout", NowUtc);

        Assert.Null(booking.CancelledByUserId);
        Assert.Null(booking.UpdatedByUserId);
    }

    // The difference from Cancel(), which does record one.
    [Fact]
    public void Cancel_StillRecordsTheActingUser()
    {
        var booking = CreateValid();

        booking.Cancel(ActorId, "Changed my mind", NowUtc);

        Assert.Equal(ActorId, booking.CancelledByUserId);
        Assert.Equal(ActorId, booking.UpdatedByUserId);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public void CancelForBlackout_RefusesATerminalStatus(BookingStatus status)
    {
        var booking = CreateValid(status);

        Assert.Throws<InvalidOperationException>(() => booking.CancelForBlackout("Blackout", NowUtc));
    }

    // ---- The cancellation window (WP-4 Phase 2b, FR-4.4) -------------------
    //
    // ReasonCodes.BookingNotCancellable has always described two halves —
    // already terminal, and already ended — and until Phase 2b only the first
    // was enforced. These cover the second.

    // The same guard CancelForBlackout has, for the same reason: nothing in this
    // system writes BookingStatus.Completed, so a meeting that happened and was
    // checked into is still Confirmed, and status alone would let a member
    // "cancel" last month's meeting and rewrite history. Cancelling something
    // that already finished frees no slot.
    [Fact]
    public void Cancel_RefusesABookingThatHasAlreadyEnded()
    {
        var booking = CreateValid();
        var afterItEnded = Ends.AddMinutes(1);

        Assert.False(booking.CanBeCancelled(afterItEnded));
        Assert.Throws<InvalidOperationException>(
            () => booking.Cancel(ActorId, "Too late", afterItEnded));
    }

    // The test is on EndsAtUtc and deliberately not StartsAtUtc: the room is
    // free from now on, which is the whole point of cancelling. Decision 0019's
    // BlackoutPeriodElapsed rule, reapplied.
    [Fact]
    public void Cancel_AllowsABookingAlreadyUnderWay()
    {
        var booking = CreateValid();
        var midway = Starts.AddMinutes(30);

        Assert.True(booking.CanBeCancelled(midway));

        booking.Cancel(ActorId, "Ending early", midway);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }

    // The instant the booking ends is already too late — the interval is
    // half-open, so EndsAtUtc is the first instant it no longer holds.
    [Fact]
    public void Cancel_RefusesABookingAtTheExactInstantItEnds()
    {
        var booking = CreateValid();

        Assert.False(booking.CanBeCancelled(Ends));
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    public void CanBeCancelled_AcceptsALiveClaimOnTheResource(BookingStatus status)
    {
        Assert.True(CreateValid(status).CanBeCancelled(NowUtc));
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Rejected)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    public void CanBeCancelled_RefusesATerminalStatus(BookingStatus status)
    {
        Assert.False(CreateValid(status).CanBeCancelled(NowUtc));
    }

    // Not idempotent, deliberately, and this is the difference from archiving a
    // resource: a second cancellation would overwrite CancelledByUserId,
    // CancelledAtUtc and the reason with a second actor's, so the record of who
    // called the meeting off would quietly change. Archive has nothing to
    // overwrite, which is why that transition can safely no-op.
    [Fact]
    public void Cancel_RefusesASecondCancellationRatherThanOverwritingTheFirst()
    {
        var booking = CreateValid();
        var firstActor = Guid.NewGuid();

        booking.Cancel(firstActor, "First reason", NowUtc);

        Assert.Throws<InvalidOperationException>(
            () => booking.Cancel(Guid.NewGuid(), "Second reason", NowUtc.AddMinutes(1)));

        Assert.Equal(firstActor, booking.CancelledByUserId);
        Assert.Equal("First reason", booking.CancellationReason);
        Assert.Equal(NowUtc, booking.CancelledAtUtc);
    }

    // The guard that protects history, and the reason it cannot be left to the
    // status alone: nothing in this system writes BookingStatus.Completed, so a
    // meeting that happened and was checked into is still Confirmed. Without
    // this, a blackout over last month would cancel it.
    [Fact]
    public void CancelForBlackout_RefusesABookingThatHasAlreadyFinished()
    {
        var booking = CreateValid();
        var afterItEnded = Ends.AddMinutes(1);

        Assert.False(booking.CanBeCancelledForBlackout(afterItEnded));
        Assert.Throws<InvalidOperationException>(() => booking.CancelForBlackout("Blackout", afterItEnded));
    }

    // In progress is not finished: the room is unusable from now on, so the
    // meeting currently in it has to stop.
    [Fact]
    public void CancelForBlackout_AllowsABookingAlreadyUnderWay()
    {
        var booking = CreateValid();
        var midway = Starts.AddMinutes(30);

        Assert.True(booking.CanBeCancelledForBlackout(midway));

        booking.CancelForBlackout("Blackout", midway);

        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }

    // The boundary: EndsAtUtc > nowUtc, so a booking ending exactly now is over.
    [Fact]
    public void CanBeCancelledForBlackout_IsFalseAtTheExactEndInstant()
    {
        var booking = CreateValid();

        Assert.False(booking.CanBeCancelledForBlackout(Ends));
    }
}
