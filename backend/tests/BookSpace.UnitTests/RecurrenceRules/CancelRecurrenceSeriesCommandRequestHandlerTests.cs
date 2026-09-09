using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.RecurrenceRules.CancelSeries;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.RecurrenceRules;

// WP-5 Phase 2. What the cancel handler decides: whose series it may touch,
// whether this one can still be cancelled, which occurrences the cascade
// reaches, and — the asymmetry worth testing, same as the single-booking
// cancel — who gets the summary notification.
//
// The actual tenant/owner filtering is a WHERE clause, proved against a real
// SQL Server in the integration suite; here the repositories are fakes that
// hand back whichever rule and occurrences the test wants, which is what
// makes every branch reachable.
public class CancelRecurrenceSeriesCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly StartDate = new(2027, 3, 8);

    private static RecurrenceRule Rule(RecurrenceStatus status = RecurrenceStatus.Active)
    {
        var rule = new RecurrenceRule(
            Guid.NewGuid(), OrgId, ResourceId, Owner, RecurrenceFrequency.Weekly, 1,
            new TimeOnly(9, 0), new TimeOnly(10, 0), StartDate, StartDate.AddYears(1), null,
            "UTC", Owner, NowUtc);

        if (status == RecurrenceStatus.Cancelled)
        {
            rule.Cancel(Owner, NowUtc);
        }

        return rule;
    }

    private static Booking Occurrence(Guid recurrenceRuleId, DateTime starts, DateTime ends) =>
        new(
            Guid.NewGuid(), OrgId, ResourceId, Owner, recurrenceRuleId,
            starts, ends, 1, "Standup", BookingStatus.Confirmed, Owner, NowUtc);

    private static CancelRecurrenceSeriesCommandRequestHandler Handler(
        FakeRecurrenceRuleRepository recurrenceRules,
        FakeSeriesBookingRepository bookings,
        Guid actorUserId,
        params Role[] roles) =>
        new(recurrenceRules, bookings, new FixedCurrentUser(actorUserId, roles), new TestClock(NowUtc));

    // ---- The happy path ------------------------------------------------------

    [Fact]
    public async Task TheOwnerCancelsTheirOwnSeriesAndEveryLiveOccurrence()
    {
        var rule = Rule();
        var first = Occurrence(rule.Id, NowUtc.AddDays(1), NowUtc.AddDays(1).AddHours(1));
        var second = Occurrence(rule.Id, NowUtc.AddDays(8), NowUtc.AddDays(8).AddHours(1));
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository { CancellableOccurrences = [first, second] };

        var response = await Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id, "No longer needed"), CancellationToken.None);

        Assert.Equal(RecurrenceStatus.Cancelled, rule.Status);
        Assert.Equal(Owner, rule.UpdatedByUserId);
        Assert.Equal(NowUtc, rule.UpdatedAtUtc);

        Assert.Equal(BookingStatus.Cancelled, first.Status);
        Assert.Equal("No longer needed", first.CancellationReason);
        Assert.Equal(Owner, first.CancelledByUserId);
        Assert.Equal(BookingStatus.Cancelled, second.Status);

        Assert.Equal(rule.Id, response.RecurrenceRuleId);
        Assert.Equal(Owner, response.CancelledByUserId);
        Assert.Equal(NowUtc, response.CancelledAtUtc);
        Assert.Equal([first.Id, second.Id], response.CancelledBookingIds);
        Assert.Equal(1, bookings.SaveChangesCount);
    }

    [Fact]
    public async Task AReasonIsOptionalAndDefaultsToAStandardText()
    {
        var rule = Rule();
        var occurrence = Occurrence(rule.Id, NowUtc.AddDays(1), NowUtc.AddDays(1).AddHours(1));
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository { CancellableOccurrences = [occurrence] };

        await Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(occurrence.CancellationReason));
    }

    [Fact]
    public async Task ASeriesWithNoRemainingLiveOccurrencesIsStillCancelled()
    {
        var rule = Rule();
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository { CancellableOccurrences = [] };

        var response = await Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Equal(RecurrenceStatus.Cancelled, rule.Status);
        Assert.Empty(response.CancelledBookingIds);
    }

    [Fact]
    public async Task TheOccurrenceQueryIsScopedToTheRuleAndTheCurrentInstant()
    {
        var rule = Rule();
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        await Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Equal(rule.Id, bookings.RequestedRecurrenceRuleId);
        Assert.Equal(NowUtc, bookings.RequestedNowUtc);
    }

    // ---- Who may cancel (decision 0002, reapplied) ---------------------------

    [Fact]
    public async Task AMemberMayOnlyReachTheirOwnSeries()
    {
        var rule = Rule();
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        await Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Equal(Owner, recurrenceRules.CancellationOwner!.UserId);
    }

    [Fact]
    public async Task AnAdminMayReachAnySeriesInTheirTenant()
    {
        var rule = Rule();
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        await Handler(recurrenceRules, bookings, Admin, Role.TenantAdmin).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Null(recurrenceRules.CancellationOwner!.UserId);
    }

    [Fact]
    public async Task AnApproverMayOnlyReachTheirOwnSeries()
    {
        var rule = Rule();
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        await Handler(recurrenceRules, bookings, Admin, Role.Approver, Role.Member).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Equal(Admin, recurrenceRules.CancellationOwner!.UserId);
    }

    [Fact]
    public async Task AnAdminCancellationRecordsTheAdminAsTheActorAndKeepsTheOwner()
    {
        var rule = Rule();
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        var response = await Handler(recurrenceRules, bookings, Admin, Role.TenantAdmin).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Equal(Admin, rule.UpdatedByUserId);
        Assert.Equal(Owner, rule.UserId);
        Assert.Equal(Admin, response.CancelledByUserId);
    }

    // ---- The notification asymmetry ------------------------------------------

    [Fact]
    public async Task SelfCancellationEnqueuesNoNotification()
    {
        var rule = Rule();
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        await Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Empty(bookings.AddedNotifications);
    }

    [Fact]
    public async Task AnAdminCancellationEnqueuesOneSummaryNotificationForTheOwner()
    {
        var rule = Rule();
        var first = Occurrence(rule.Id, NowUtc.AddDays(1), NowUtc.AddDays(1).AddHours(1));
        var second = Occurrence(rule.Id, NowUtc.AddDays(8), NowUtc.AddDays(8).AddHours(1));
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository { CancellableOccurrences = [first, second] };

        await Handler(recurrenceRules, bookings, Admin, Role.TenantAdmin).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        // One notification total, not one per occurrence (owner's answer,
        // 2026-09-08) — the point of the mixed one-vs-two-occurrence setup.
        var notification = Assert.Single(bookings.AddedNotifications);
        Assert.Equal(NotificationKind.SeriesCancelled, notification.Kind);
        Assert.Equal(rule.Id, notification.RecurrenceRuleId);
        Assert.Null(notification.BookingId);
        Assert.Null(notification.OccurrenceDate);
        Assert.Equal(Owner, notification.RecipientUserId);
        Assert.Equal(Admin, notification.CreatedByUserId);
        Assert.Equal(NowUtc, notification.SendAtUtc);
    }

    [Fact]
    public async Task AnAdminCancellingTheirOwnSeriesEnqueuesNoNotification()
    {
        var rule = new RecurrenceRule(
            Guid.NewGuid(), OrgId, ResourceId, Admin, RecurrenceFrequency.Weekly, 1,
            new TimeOnly(9, 0), new TimeOnly(10, 0), StartDate, StartDate.AddYears(1), null,
            "UTC", Admin, NowUtc);
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        await Handler(recurrenceRules, bookings, Admin, Role.TenantAdmin).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Empty(bookings.AddedNotifications);
    }

    // ---- The refusals ---------------------------------------------------------

    [Fact]
    public async Task AnInvisibleSeriesIsRecurrenceRuleNotFound()
    {
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = null };
        var bookings = new FakeSeriesBookingRepository();

        var exception = await Assert.ThrowsAsync<RecurrenceRuleNotFoundException>(
            () => Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
                new CancelRecurrenceSeriesCommandRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ErrorKind.NotFound, exception.Kind);
        Assert.Equal(ReasonCodes.RecurrenceRuleNotFound, exception.ReasonCode);
        Assert.Equal(0, recurrenceRules.SaveChangesCount);
    }

    [Fact]
    public async Task AnAlreadyCancelledSeriesIsRecurrenceRuleNotCancellable()
    {
        var rule = Rule(RecurrenceStatus.Cancelled);
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        var exception = await Assert.ThrowsAsync<RecurrenceRuleNotCancellableException>(
            () => Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
                new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None));

        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(ReasonCodes.RecurrenceRuleNotCancellable, exception.ReasonCode);
    }

    [Fact]
    public async Task NothingIsSavedWhenTheSeriesCannotBeCancelled()
    {
        var rule = Rule(RecurrenceStatus.Cancelled);
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository();

        await Assert.ThrowsAsync<RecurrenceRuleNotCancellableException>(
            () => Handler(recurrenceRules, bookings, Owner, Role.Member).Handle(
                new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None));

        Assert.Equal(0, bookings.SaveChangesCount);
        Assert.Empty(bookings.AddedNotifications);
    }

    // ---- Wiring -----------------------------------------------------------

    [Fact]
    public async Task ItRefusesToRunWithoutAnAuthenticatedUser()
    {
        var handler = new CancelRecurrenceSeriesCommandRequestHandler(
            new FakeRecurrenceRuleRepository { Cancellable = Rule() },
            new FakeSeriesBookingRepository(),
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(new CancelRecurrenceSeriesCommandRequest(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task TheRuleTheOccurrencesAndTheNotificationShareOneSave()
    {
        var rule = Rule();
        var occurrence = Occurrence(rule.Id, NowUtc.AddDays(1), NowUtc.AddDays(1).AddHours(1));
        var recurrenceRules = new FakeRecurrenceRuleRepository { Cancellable = rule };
        var bookings = new FakeSeriesBookingRepository { CancellableOccurrences = [occurrence] };

        await Handler(recurrenceRules, bookings, Admin, Role.TenantAdmin).Handle(
            new CancelRecurrenceSeriesCommandRequest(rule.Id), CancellationToken.None);

        Assert.Equal(1, bookings.SaveChangesCount);
        Assert.Single(bookings.AddedNotifications);
    }
}
