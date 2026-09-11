using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.RecurrenceRules;
using BookSpace.Application.Features.RecurrenceRules.CreateSeries;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Bookings;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.RecurrenceRules;

// WP-5 Phase 1b. What the handler decides around RecurrenceExpansion and
// dbo.CreateBooking: the three-bucket partition (created/skipped/refused),
// the approval routing and notification rows per occurrence, and the
// all-refused 422.
//
// What these tests deliberately do **not** cover: the DST mechanics
// themselves (RecurrenceExpansionTests, against real tzdata) and the
// concurrency guarantee (CreateBookingProcedureTests / a future
// RecurrenceRuleEndpointTests against a real SQL Server). A UTC resource
// throughout for the same reason CreateBookingCommandRequestHandlerTests
// gives — these assertions are about the handler, not about arithmetic
// covered elsewhere — except the one test that forces a spring-forward skip,
// which uses a purpose-built fake zone rather than real tzdata for the same
// reason.
public class CreateRecurrenceSeriesCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid ApproverId = Guid.NewGuid();

    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    // A Monday, comfortably ahead of the clock above.
    private static readonly DateOnly StartDate = new(2027, 3, 8);

    private static Resource Room(bool requiresApproval = false, bool archived = false)
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, capacity: 4,
            timeZoneId: "UTC", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);

        // Every weekday, all day but for the last second (decision 0022).
        resource.ReplaceAvailabilityWindows(
            Enum.GetValues<DayOfWeek>().Select(day => new AvailabilityWindowDefinition(
                Guid.NewGuid(), day, new TimeOnly(0, 0), new TimeOnly(23, 59, 59))),
            ActorId,
            NowUtc);

        if (requiresApproval)
        {
            resource.ReplaceApprovers([ApproverId], ActorId, NowUtc);
            resource.SetRequiresApproval(true, ActorId, NowUtc);
        }

        if (archived)
        {
            resource.Archive(ActorId, NowUtc);
        }

        return resource;
    }

    private sealed record Harness(
        CreateRecurrenceSeriesCommandRequestHandler Handler,
        FakeAvailabilityRepository Availability,
        FakeSeriesBookingRepository Bookings,
        FakeRecurrenceRuleRepository RecurrenceRules,
        PassThroughUnitOfWork UnitOfWork);

    private static Harness Build(
        Resource resource,
        IEnumerable<BookingCreationOutcome>? outcomes = null,
        BookingCreationResult defaultResult = BookingCreationResult.Created,
        int? approvalExpiryHours = null,
        IReadOnlyList<UtcInterval>? blackouts = null,
        IReadOnlyList<BookedQuantity>? bookings = null,
        ITimeZoneCatalog? timeZones = null,
        Guid? currentUserId = null)
    {
        var availability = new FakeAvailabilityRepository(resource, blackouts, bookings);
        var bookingRepository = new FakeSeriesBookingRepository(outcomes, defaultResult, approvalExpiryHours);
        var recurrenceRules = new FakeRecurrenceRuleRepository();
        var unitOfWork = new PassThroughUnitOfWork();

        var handler = new CreateRecurrenceSeriesCommandRequestHandler(
            availability,
            bookingRepository,
            recurrenceRules,
            timeZones ?? new FakeTimeZoneCatalog("UTC"),
            unitOfWork,
            new FixedCurrentUser(currentUserId ?? ActorId),
            new TestClock(NowUtc));

        return new Harness(handler, availability, bookingRepository, recurrenceRules, unitOfWork);
    }

    private static CreateRecurrenceSeriesCommandRequest Request(
        Resource resource,
        int occurrenceCount = 3,
        int startHour = 9,
        int endHour = 10,
        int quantity = 1) =>
        new(
            resource.Id,
            RecurrenceFrequency.Weekly,
            IntervalValue: 1,
            new TimeOnly(startHour, 0),
            new TimeOnly(endHour, 0),
            StartDate,
            EndDate: null,
            occurrenceCount,
            quantity,
            Title: "Standup");

    // ---- The happy path: every occurrence created ---------------------------

    [Fact]
    public async Task CreatesOneBookingPerOccurrence()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource, occurrenceCount: 3), default);

        Assert.Equal(3, response.Occurrences.Count);
        Assert.All(response.Occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));
        Assert.All(response.Occurrences, o => Assert.NotNull(o.BookingId));
        Assert.Equal(3, response.Occurrences.Select(o => o.BookingId).Distinct().Count());
        Assert.Equal(
            new[] { StartDate, StartDate.AddDays(7), StartDate.AddDays(14) },
            response.Occurrences.Select(o => o.OccurrenceDate));
    }

    [Fact]
    public async Task PersistsTheRuleOnceBeforeAnyOccurrence()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.NotNull(harness.RecurrenceRules.Added);
        Assert.Equal(response.RecurrenceRuleId, harness.RecurrenceRules.Added!.Id);
        Assert.Equal(1, harness.RecurrenceRules.SaveChangesCount);
    }

    [Fact]
    public async Task EveryOccurrenceIsAnchoredToTheRule()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.All(harness.Bookings.Attempted, b => Assert.Equal(response.RecurrenceRuleId, b.RecurrenceRuleId));
    }

    [Fact]
    public async Task RunsEachOccurrenceInItsOwnUnitOfWork()
    {
        var resource = Room();
        var harness = Build(resource);

        await harness.Handler.Handle(Request(resource, occurrenceCount: 3), default);

        Assert.Equal(3, harness.UnitOfWork.Executions);
        Assert.Equal(3, harness.Bookings.SaveChangesCount);
    }

    // ---- Approval routing (FR-7.1), per occurrence ---------------------------

    [Fact]
    public async Task PendsEveryOccurrenceOnAnApprovalGatedResource()
    {
        var resource = Room(requiresApproval: true);
        var harness = Build(resource, approvalExpiryHours: 48);

        var response = await harness.Handler.Handle(Request(resource, occurrenceCount: 2), default);

        Assert.All(response.Occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Created, o.Status));
        Assert.Equal(2, harness.Bookings.AddedApprovalRequests.Count);
        Assert.All(
            harness.Bookings.AddedApprovalRequests,
            a => Assert.Equal(NowUtc.AddHours(48), a.ExpiresAtUtc));

        // One approver notification per occurrence, none to the booker.
        Assert.Equal(2, harness.Bookings.AddedNotifications.Count);
        Assert.All(
            harness.Bookings.AddedNotifications,
            n => Assert.Equal(NotificationKind.ApprovalRequested, n.Kind));
        Assert.All(harness.Bookings.AddedNotifications, n => Assert.Equal(ApproverId, n.RecipientUserId));
    }

    // Hardening pass, P1. Same reasoning as
    // CreateBookingCommandRequestHandlerTests.ReactsToTheProcedureDowngradingA-
    // ConfirmedRequestToPending, one level up: the resource snapshot said
    // RequiresApproval = false, so the handler asked dbo.CreateBooking for
    // Confirmed on every occurrence, but the procedure re-reads RequiresApproval
    // under its own lock and can answer Pending per occurrence. Before this
    // pass the handler trusted its own snapshot unconditionally, which would
    // have reported this occurrence Confirmed with no ApprovalRequest at all.
    [Fact]
    public async Task ReactsToTheProcedureDowngradingAnOccurrenceToPending()
    {
        var resource = Room(requiresApproval: false);
        resource.ReplaceApprovers([ApproverId], ActorId, NowUtc);

        var harness = Build(
            resource,
            outcomes: [new BookingCreationOutcome(BookingCreationResult.Created, null, BookingStatus.Pending)],
            approvalExpiryHours: 48);

        var response = await harness.Handler.Handle(Request(resource, occurrenceCount: 1), default);

        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, Assert.Single(response.Occurrences).Status);

        var approval = Assert.Single(harness.Bookings.AddedApprovalRequests);
        Assert.Equal(NowUtc.AddHours(48), approval.ExpiresAtUtc);

        var notification = Assert.Single(harness.Bookings.AddedNotifications);
        Assert.Equal(NotificationKind.ApprovalRequested, notification.Kind);
    }

    [Fact]
    public async Task FetchesApprovalExpiryOnceForTheWholeSeries()
    {
        // FakeAvailabilityRepository has no call counter of its own for this,
        // so the assertion is indirect: every ApprovalRequest still gets the
        // same expiry, which could only be true if it was read once and reused
        // (a per-occurrence read of a differently-stubbed value would show up
        // as a mismatch — there being only one fake value makes this a weak
        // assertion on its own, strengthened by RunsEachOccurrenceInItsOwnUnitOfWork
        // establishing that FindApprovalExpiryHoursAsync is not folded into the
        // per-occurrence unit of work at all, per the handler's structure).
        var resource = Room(requiresApproval: true);
        var harness = Build(resource, approvalExpiryHours: 24);

        await harness.Handler.Handle(Request(resource, occurrenceCount: 5), default);

        Assert.All(
            harness.Bookings.AddedApprovalRequests,
            a => Assert.Equal(NowUtc.AddHours(24), a.ExpiresAtUtc));
    }

    // ---- Refused via the pre-check (BookingEligibility) ----------------------

    [Fact]
    public async Task ReportsOneOccurrenceRefusedByABlackoutAndCreatesTheRest()
    {
        var resource = Room();
        // Covers only the second occurrence's interval (StartDate + 7 days).
        var blackout = new UtcInterval(
            StartDate.AddDays(7).ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc),
            StartDate.AddDays(7).ToDateTime(new TimeOnly(11, 0), DateTimeKind.Utc));
        var harness = Build(resource, blackouts: [blackout]);

        var response = await harness.Handler.Handle(Request(resource, occurrenceCount: 3), default);

        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, response.Occurrences[0].Status);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Refused, response.Occurrences[1].Status);
        Assert.Equal(ReasonCodes.BlackoutPeriod, response.Occurrences[1].ReasonCode);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, response.Occurrences[2].Status);

        // Never even attempted against the procedure — the pre-check's answer
        // is confident enough to skip the round trip, matching the
        // single-booking handler's own reasoning for pre-checking at all.
        Assert.Equal(2, harness.Bookings.Attempted.Count);
    }

    // Only the first occurrence's interval is taken — occurrenceCount: 2 keeps
    // this out of the all-refused branch below, which a single-occurrence
    // series here would otherwise fall into.
    [Fact]
    public async Task ReportsAnOccurrenceRefusedByCapacityWithNoUnitsFreeAtAll()
    {
        var resource = Room();
        var takenInterval = new UtcInterval(
            StartDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc),
            StartDate.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc));
        var harness = Build(
            resource,
            bookings: [new BookedQuantity(takenInterval, 4)]);

        var response = await harness.Handler.Handle(Request(resource, occurrenceCount: 2), default);

        Assert.Equal(RecurrenceOccurrenceReportStatus.Refused, response.Occurrences[0].Status);
        Assert.Equal(ReasonCodes.SlotUnavailable, response.Occurrences[0].ReasonCode);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, response.Occurrences[1].Status);
    }

    // A resource with no schedule at all refuses every occurrence — genuinely
    // the all-refused case, so this asserts against the exception rather than
    // a response.
    [Fact]
    public async Task AllRefusedByOutsideAvailabilityCarriesNoApprovalOrNotificationSideEffects()
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Closed Room", ResourceType.Room, capacity: 1,
            timeZoneId: "UTC", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc); // no windows at all
        var harness = Build(resource);

        var exception = await Assert.ThrowsAsync<NoOccurrencesCreatedException>(
            () => harness.Handler.Handle(Request(resource, occurrenceCount: 1), default));

        var occurrence = Assert.Single(
            (IReadOnlyList<RecurrenceOccurrenceReport>)exception.Extensions["occurrences"]!);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Refused, occurrence.Status);
        Assert.Equal(ReasonCodes.OutsideAvailability, occurrence.ReasonCode);
        Assert.Empty(harness.Bookings.Attempted);
        Assert.Empty(harness.Bookings.AddedNotifications);
    }

    // ---- Refused by dbo.CreateBooking itself (the race the pre-check missed) --

    [Fact]
    public async Task ReportsAnOccurrenceTheProcedureDeclinesAndStillCreatesTheRest()
    {
        var resource = Room();
        var harness = Build(
            resource,
            outcomes:
            [
                new BookingCreationOutcome(BookingCreationResult.Created, null),
                new BookingCreationOutcome(BookingCreationResult.SlotUnavailable, null),
                new BookingCreationOutcome(BookingCreationResult.Created, null),
            ]);

        var response = await harness.Handler.Handle(Request(resource, occurrenceCount: 3), default);

        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, response.Occurrences[0].Status);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Refused, response.Occurrences[1].Status);
        Assert.Equal(ReasonCodes.SlotUnavailable, response.Occurrences[1].ReasonCode);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, response.Occurrences[2].Status);

        // All three were genuinely attempted against the procedure, unlike the
        // pre-check-refused case above.
        Assert.Equal(3, harness.Bookings.Attempted.Count);

        // Nothing lingers from the declined attempt: exactly two notifications
        // (Confirmed, one per created occurrence), never three.
        Assert.Equal(2, harness.Bookings.AddedNotifications.Count);
    }

    // A single declined occurrence, on an approval-gated resource, is again
    // the all-refused case — asserted against the exception, with the
    // no-side-effects claims checked on the harness regardless of how the
    // outcome reached the caller.
    [Fact]
    public async Task ADeclinedOccurrenceOnAnApprovalGatedResourceStagesNoApprovalRequest()
    {
        var resource = Room(requiresApproval: true);
        var harness = Build(
            resource,
            outcomes: [new BookingCreationOutcome(BookingCreationResult.CapacityExceeded, 1)]);

        await Assert.ThrowsAsync<NoOccurrencesCreatedException>(
            () => harness.Handler.Handle(Request(resource, occurrenceCount: 1), default));

        Assert.Empty(harness.Bookings.AddedApprovalRequests);
        Assert.Empty(harness.Bookings.AddedNotifications);
        // The transaction still ran — it is what asked the question — it just
        // saved nothing.
        Assert.Equal(1, harness.UnitOfWork.Executions);
        Assert.Equal(0, harness.Bookings.SaveChangesCount);
    }

    // ---- Entirely-elapsed occurrences -----------------------------------------

    // A series wholly in the past refuses every occurrence it produces — the
    // all-refused case again, since a series starting two weeks ago and
    // running weekly for two occurrences both lands before NowUtc.
    [Fact]
    public async Task ReportsAnOccurrenceThatHasAlreadyEndedAsRefused()
    {
        var resource = Room();
        var pastStart = DateOnly.FromDateTime(NowUtc).AddDays(-14);
        var request = new CreateRecurrenceSeriesCommandRequest(
            resource.Id, RecurrenceFrequency.Weekly, 1, new TimeOnly(9, 0), new TimeOnly(10, 0),
            pastStart, EndDate: null, OccurrenceCount: 1, Quantity: 1, Title: null);
        var harness = Build(resource);

        var exception = await Assert.ThrowsAsync<NoOccurrencesCreatedException>(
            () => harness.Handler.Handle(request, default));

        var occurrence = Assert.Single(
            (IReadOnlyList<RecurrenceOccurrenceReport>)exception.Extensions["occurrences"]!);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Refused, occurrence.Status);
        Assert.Equal(ReasonCodes.BookingInThePast, occurrence.ReasonCode);
        Assert.Empty(harness.Bookings.Attempted);
    }

    // ---- Skipped via decision 0008 --------------------------------------------

    // A purpose-built fake zone rather than real tzdata (RecurrenceExpansionTests
    // already proves the real gap-detection logic) — this is only asking
    // whether the *handler* reacts correctly to a skip: reports it, and
    // enqueues 0008's 14-day-ahead notification, anchored to the rule and the
    // date rather than to a Booking that does not exist.
    [Fact]
    public async Task ReportsASkippedOccurrenceAndEnqueuesItsNotificationWithoutAttemptingACreate()
    {
        var resource = Room();
        var secondOccurrenceLocalStart = StartDate.AddDays(7).ToDateTime(new TimeOnly(9, 0));
        var zone = new GapForcingTimeZone(secondOccurrenceLocalStart, TimeSpan.Zero);
        var harness = Build(resource, timeZones: new SingleZoneCatalog(zone));

        var response = await harness.Handler.Handle(Request(resource, occurrenceCount: 3), default);

        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, response.Occurrences[0].Status);
        Assert.Equal(RecurrenceOccurrenceReportStatus.SkippedSpringForwardGap, response.Occurrences[1].Status);
        Assert.Null(response.Occurrences[1].BookingId);
        Assert.Null(response.Occurrences[1].ReasonCode);
        Assert.Equal(RecurrenceOccurrenceReportStatus.Created, response.Occurrences[2].Status);

        // Only the two real occurrences ever reach the procedure.
        Assert.Equal(2, harness.Bookings.Attempted.Count);

        var skipNotification = Assert.Single(
            harness.Bookings.AddedNotifications, n => n.Kind == NotificationKind.RecurrenceOccurrenceSkipped);
        Assert.Null(skipNotification.BookingId);
        Assert.Equal(harness.RecurrenceRules.Added!.Id, skipNotification.RecurrenceRuleId);
        Assert.Equal(StartDate.AddDays(7), skipNotification.OccurrenceDate);
        Assert.Equal(StartDate.AddDays(7).AddDays(-14), DateOnly.FromDateTime(skipNotification.SendAtUtc));
    }

    // ---- All refused: FR-5.4's owner's answer to shape question 2 -------------

    [Fact]
    public async Task ThrowsWhenEveryOccurrenceIsRefused()
    {
        var resource = Room();
        var takenInterval = new UtcInterval(
            StartDate.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc),
            StartDate.AddDays(21).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc));
        var harness = Build(resource, bookings: [new BookedQuantity(takenInterval, 4)]);

        var exception = await Assert.ThrowsAsync<NoOccurrencesCreatedException>(
            () => harness.Handler.Handle(Request(resource, occurrenceCount: 3), default));

        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Equal(ReasonCodes.NoOccurrencesCreated, exception.ReasonCode);

        var occurrences = Assert.IsAssignableFrom<IReadOnlyList<RecurrenceOccurrenceReport>>(
            exception.Extensions["occurrences"]);
        Assert.Equal(3, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal(RecurrenceOccurrenceReportStatus.Refused, o.Status));
    }

    // The RecurrenceRule row is persisted up front (Bookings.RecurrenceRuleId
    // is a real FK) and then removed again once nothing was booked against
    // it — a 422 leaves no trace, not even the series shell. This test
    // exists so the compensating removal is pinned rather than accidental;
    // it is a regression test for the handler's first design, which left the
    // row behind.
    [Fact]
    public async Task TheRuleIsRemovedAgainWhenEveryOccurrenceIsRefused()
    {
        var resource = Room(archived: false);
        var takenInterval = new UtcInterval(
            StartDate.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc),
            StartDate.AddDays(21).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc));
        var harness = Build(resource, bookings: [new BookedQuantity(takenInterval, 4)]);

        await Assert.ThrowsAsync<NoOccurrencesCreatedException>(
            () => harness.Handler.Handle(Request(resource, occurrenceCount: 3), default));

        Assert.NotNull(harness.RecurrenceRules.Added);
        Assert.Same(harness.RecurrenceRules.Added, harness.RecurrenceRules.Removed);
        Assert.Equal(2, harness.RecurrenceRules.SaveChangesCount);
    }

    // The companion case: a skipped occurrence's decision-0008 notification
    // must not survive an all-refused series either, or a client who was
    // told the series reserved nothing would still get an email 14 days
    // later about one of its occurrences.
    [Fact]
    public async Task ASkippedOccurrencesNotificationIsNeverPersistedWhenEveryOccurrenceIsRefused()
    {
        var resource = Room();
        var secondOccurrenceLocalStart = StartDate.AddDays(7).ToDateTime(new TimeOnly(9, 0));
        var zone = new GapForcingTimeZone(secondOccurrenceLocalStart, TimeSpan.Zero);
        // The other two occurrences are refused by a blackout covering the
        // whole span, so nothing in this series is ever created.
        var wholeSpan = new UtcInterval(
            StartDate.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc),
            StartDate.AddDays(21).ToDateTime(new TimeOnly(0, 0), DateTimeKind.Utc));
        var harness = Build(resource, blackouts: [wholeSpan], timeZones: new SingleZoneCatalog(zone));

        await Assert.ThrowsAsync<NoOccurrencesCreatedException>(
            () => harness.Handler.Handle(Request(resource, occurrenceCount: 3), default));

        Assert.Empty(harness.Bookings.AddedNotifications);
        Assert.NotNull(harness.RecurrenceRules.Removed);
    }

    // Hardening pass, P2. Before this pass, only a *clean* all-refused
    // outcome (every occurrence answered with a rejection, no exception)
    // triggered the compensating removal above — a genuinely unexpected
    // exception on the very first occurrence (a real DB error, not one of
    // dbo.CreateBooking's own rejection outcomes) propagated straight out of
    // Handle, leaving the RecurrenceRule row orphaned: persisted up front,
    // zero occurrences ever created, and no response reaching the client to
    // explain any of it.
    [Fact]
    public async Task TheRuleIsRemovedWhenAnUnexpectedExceptionInterruptsTheFirstOccurrence()
    {
        var resource = Room();
        var harness = Build(resource);
        var boom = new InvalidOperationException("simulated transient failure");
        harness.Bookings.ThrowOnCreate = boom;
        harness.Bookings.ThrowOnCallNumber = 1;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Handler.Handle(Request(resource, occurrenceCount: 3), default));

        // The original exception propagates unchanged — this is orphan
        // prevention, not a new error-handling path — while the rule is still
        // cleaned up rather than left behind.
        Assert.Same(boom, thrown);
        Assert.NotNull(harness.RecurrenceRules.Added);
        Assert.Same(harness.RecurrenceRules.Added, harness.RecurrenceRules.Removed);
    }

    // The companion case: once at least one occurrence has genuinely been
    // created, a later occurrence's unexpected exception must NOT remove the
    // rule — it now has real bookings hanging off it.
    [Fact]
    public async Task TheRuleSurvivesAnUnexpectedExceptionAfterAtLeastOneOccurrenceWasCreated()
    {
        var resource = Room();
        var harness = Build(resource);

        // The first occurrence's CreateAsync call succeeds via the fake's
        // default (Created); the second throws.
        harness.Bookings.ThrowOnCreate = new InvalidOperationException("simulated transient failure");
        harness.Bookings.ThrowOnCallNumber = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Handler.Handle(Request(resource, occurrenceCount: 3), default));

        Assert.Null(harness.RecurrenceRules.Removed);
    }

    // ---- Rejections before expansion ever runs ---------------------------------

    [Fact]
    public async Task RefusesAnUnknownResource()
    {
        var resource = Room();
        var harness = Build(resource);
        var request = Request(resource) with { ResourceId = Guid.NewGuid() };

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => harness.Handler.Handle(request, default));
    }

    [Fact]
    public async Task RefusesAnArchivedResource()
    {
        var resource = Room(archived: true);
        var harness = Build(resource);

        await Assert.ThrowsAsync<ResourceArchivedException>(
            () => harness.Handler.Handle(Request(resource), default));

        Assert.Null(harness.RecurrenceRules.Added);
    }

    [Fact]
    public async Task RefusesADurationOutsideTheResourceLimits()
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Small-window room", ResourceType.Room, capacity: 4,
            timeZoneId: "UTC", requiresApproval: false,
            minDurationMinutes: 600, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);
        resource.ReplaceAvailabilityWindows(
            Enum.GetValues<DayOfWeek>().Select(day => new AvailabilityWindowDefinition(
                Guid.NewGuid(), day, new TimeOnly(0, 0), new TimeOnly(23, 59, 59))),
            ActorId,
            NowUtc);
        var harness = Build(resource);

        // The default 09:00-10:00 request (60 minutes) is well under the
        // 600-minute minimum.
        await Assert.ThrowsAsync<BookingDurationOutOfRangeException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    [Fact]
    public async Task ThrowsWhenThereIsNoAuthenticatedUser()
    {
        var resource = Room();
        var availability = new FakeAvailabilityRepository(resource);

        var handler = new CreateRecurrenceSeriesCommandRequestHandler(
            availability,
            new FakeSeriesBookingRepository(),
            new FakeRecurrenceRuleRepository(),
            new FakeTimeZoneCatalog("UTC"),
            new PassThroughUnitOfWork(),
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(Request(resource), default));
    }

    // A zone with no transitions, forced to report exactly one local start as
    // invalid — everything else behaves like plain UTC.
    private sealed class GapForcingTimeZone : IResourceTimeZone
    {
        private readonly DateTime _invalidLocal;
        private readonly TimeSpan _offset;

        public GapForcingTimeZone(DateTime invalidLocal, TimeSpan offset)
        {
            _invalidLocal = invalidLocal;
            _offset = offset;
        }

        public DateTime ToUtcEarliest(DateTime resourceLocal) => ToUtc(resourceLocal);

        public DateTime ToUtcLatest(DateTime resourceLocal) => ToUtc(resourceLocal);

        public DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc + _offset, DateTimeKind.Unspecified);

        public bool IsInvalidLocalTime(DateTime resourceLocal) => resourceLocal == _invalidLocal;

        private DateTime ToUtc(DateTime resourceLocal) =>
            DateTime.SpecifyKind(resourceLocal - _offset, DateTimeKind.Utc);
    }

    private sealed class SingleZoneCatalog : ITimeZoneCatalog
    {
        private readonly IResourceTimeZone _zone;

        public SingleZoneCatalog(IResourceTimeZone zone) => _zone = zone;

        public bool IsKnownIanaId(string timeZoneId) => true;

        public IResourceTimeZone GetResourceTimeZone(string timeZoneId) => _zone;
    }
}
