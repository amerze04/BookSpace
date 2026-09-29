using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests;

public class NotificationTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ForBooking_AnchorsToBookingOnly()
    {
        var bookingId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var sendAt = NowUtc.AddHours(1);

        var notification = Notification.ForBooking(Guid.NewGuid(), bookingId, recipientId, NotificationKind.Confirmed, sendAt, actorId, NowUtc);

        Assert.Equal(bookingId, notification.BookingId);
        Assert.Null(notification.RecurrenceRuleId);
        Assert.Null(notification.OccurrenceDate);
        Assert.Equal(NotificationKind.Confirmed, notification.Kind);
        Assert.Equal(actorId, notification.CreatedByUserId);
        Assert.Equal(0, notification.Attempts);
    }

    [Fact]
    public void ForSkippedOccurrence_AnchorsToRecurrenceRuleAndDate_WithNoHumanCreator()
    {
        var recurrenceRuleId = Guid.NewGuid();
        var occurrenceDate = new DateOnly(2027, 3, 14);
        var recipientId = Guid.NewGuid();
        var sendAt = NowUtc.AddDays(14);

        var notification = Notification.ForSkippedOccurrence(Guid.NewGuid(), recurrenceRuleId, occurrenceDate, recipientId, sendAt, NowUtc);

        Assert.Null(notification.BookingId);
        Assert.Equal(recurrenceRuleId, notification.RecurrenceRuleId);
        Assert.Equal(occurrenceDate, notification.OccurrenceDate);
        Assert.Equal(NotificationKind.RecurrenceOccurrenceSkipped, notification.Kind);
        Assert.Null(notification.CreatedByUserId);
    }

    [Fact]
    public void MarkOutcome_OnSuccess_SetsSentAtUtcAndClearsError_WithoutTouchingAttempts()
    {
        var notification = Notification.ForBooking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationKind.Reminder, NowUtc, null, NowUtc);
        notification.MarkOutcome(NowUtc.AddMinutes(1), succeeded: false, error: "SMTP timeout");

        var sentAt = NowUtc.AddMinutes(2);
        notification.MarkOutcome(sentAt, succeeded: true);

        // WP-8 Phase 2: Attempts is owned by the claim step
        // (INotificationRepository.ClaimDueAsync), never by MarkOutcome — see
        // MarkOutcome's own header for why incrementing here too would
        // double-count every attempt against MaxAttempts' retry cap.
        Assert.Equal(0, notification.Attempts);
        Assert.Equal(sentAt, notification.SentAtUtc);
        Assert.Null(notification.LastError);
    }

    [Fact]
    public void MarkOutcome_OnFailure_SetsLastErrorAndLeavesSentAtUtcNull()
    {
        var notification = Notification.ForBooking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationKind.Reminder, NowUtc, null, NowUtc);

        notification.MarkOutcome(NowUtc.AddMinutes(1), succeeded: false, error: "SMTP timeout");

        Assert.Equal(0, notification.Attempts);
        Assert.Null(notification.SentAtUtc);
        Assert.Equal("SMTP timeout", notification.LastError);
    }

    [Fact]
    public void ReminderSendAtUtc_ReturnsTheLeadTimeWhenItIsStillInTheFuture()
    {
        var startsAtUtc = NowUtc.AddHours(2);

        var result = Notification.ReminderSendAtUtc(startsAtUtc, leadMinutes: 30, NowUtc);

        Assert.Equal(startsAtUtc.AddMinutes(-30), result);
    }

    [Fact]
    public void ReminderSendAtUtc_ClampsToNowWhenTheLeadTimeHasAlreadyPassed()
    {
        // Decision D4 (docs/wp8-plan.md): a short-notice booking confirmed
        // inside its own lead time is reminded once, immediately, rather than
        // silently never — never a past instant.
        var startsAtUtc = NowUtc.AddMinutes(10);

        var result = Notification.ReminderSendAtUtc(startsAtUtc, leadMinutes: 60, NowUtc);

        Assert.Equal(NowUtc, result);
    }
}
