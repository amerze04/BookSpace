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
}
