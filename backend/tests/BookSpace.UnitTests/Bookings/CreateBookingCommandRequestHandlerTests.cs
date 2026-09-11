using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Bookings.CreateBooking;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.Bookings;

// WP-4 Phase 1c. What the handler decides before and around dbo.CreateBooking:
// the rejection order, the approval routing, the notification rows, and the
// mapping of the procedure's answer onto reason codes.
//
// What these tests deliberately do **not** cover is the guarantee itself. No
// fake can simulate a range lock, so AC-1 is proved in
// CreateBookingProcedureTests against a real SQL Server. Here the procedure is a
// fake that returns whichever result code the test wants, which is what makes
// every rejection path reachable without arranging real contention.
//
// A UTC resource throughout: the conversion rules have thorough tests of their
// own (SystemResourceTimeZoneTests), and a zone whose local time is UTC keeps
// these assertions about the handler rather than about arithmetic the test would
// have to redo.
public class CreateBookingCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid ApproverId = Guid.NewGuid();

    // A Thursday, comfortably ahead of the clock below.
    private static DateTime At(int hour, int minute = 0) =>
        new(2027, 3, 11, hour, minute, 0, DateTimeKind.Utc);

    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    private static Resource Room(
        int capacity = 4,
        bool requiresApproval = false,
        int? minDurationMinutes = null,
        int? maxDurationMinutes = null,
        bool archived = false,
        bool open = true)
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, capacity,
            timeZoneId: "UTC", requiresApproval: false,
            minDurationMinutes: minDurationMinutes, maxDurationMinutes: maxDurationMinutes,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);

        if (open)
        {
            // Every weekday, all day but for the last second — the midnight
            // convention (decision 0022) then makes it a continuous span.
            resource.ReplaceAvailabilityWindows(
                Enum.GetValues<DayOfWeek>().Select(day => new AvailabilityWindowDefinition(
                    Guid.NewGuid(), day, new TimeOnly(0, 0), new TimeOnly(23, 59, 59))),
                ActorId,
                NowUtc);
        }

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
        CreateBookingCommandRequestHandler Handler,
        FakeAvailabilityRepository Availability,
        FakeBookingRepository Bookings,
        PassThroughUnitOfWork UnitOfWork);

    private static Harness Build(
        Resource resource,
        BookingCreationResult result = BookingCreationResult.Created,
        int? remainingCapacity = null,
        int? approvalExpiryHours = null,
        IReadOnlyList<UtcInterval>? blackouts = null,
        IReadOnlyList<BookedQuantity>? bookings = null,
        Guid? currentUserId = null,
        BookingStatus? actualStatusOverride = null)
    {
        var availability = new FakeAvailabilityRepository(resource, blackouts, bookings);
        var bookingRepository = new FakeBookingRepository(
            result, remainingCapacity, approvalExpiryHours, actualStatusOverride);
        var unitOfWork = new PassThroughUnitOfWork();

        var handler = new CreateBookingCommandRequestHandler(
            availability,
            bookingRepository,
            new FakeTimeZoneCatalog("UTC"),
            unitOfWork,
            new FixedCurrentUser(currentUserId ?? ActorId),
            new TestClock(NowUtc));

        return new Harness(handler, availability, bookingRepository, unitOfWork);
    }

    private static CreateBookingCommandRequest Request(
        Resource resource,
        int startHour = 9,
        int endHour = 10,
        int quantity = 1,
        string? title = "Design review") =>
        new(resource.Id, At(startHour), At(endHour), quantity, title);

    // ---- The happy path ----------------------------------------------------

    [Fact]
    public async Task Confirms_ABookingOnAnOpenResource()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.Equal(BookingStatus.Confirmed, response.Status);
        Assert.Equal(resource.Id, response.ResourceId);
        Assert.Equal(ActorId, response.UserId);
        Assert.Equal(At(9), response.StartsAtUtc);
        Assert.Equal(At(10), response.EndsAtUtc);
        Assert.Equal("Design review", response.Title);
        Assert.Null(response.Approval);
    }

    // The response's CreatedAtUtc and the instant handed to the procedure are the
    // same value, which is what CLAUDE.md §4.3's truncated clock is for: a stamp
    // taken separately in SQL could round to a different second than the one the
    // client is told.
    [Fact]
    public async Task StampsTheSameInstantOnTheRowAndTheResponse()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.Equal(NowUtc, response.CreatedAtUtc);
        Assert.Equal(NowUtc, harness.Bookings.Created!.NowUtc);
    }

    // The id in the response is the one the procedure was given — not a second
    // one minted along the way.
    [Fact]
    public async Task PassesTheResponseIdToTheProcedure()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.Equal(response.Id, harness.Bookings.Created!.Id);
        Assert.NotEqual(Guid.Empty, response.Id);
    }

    [Fact]
    public async Task RunsTheWriteInsideTheUnitOfWork()
    {
        var resource = Room();
        var harness = Build(resource);

        await harness.Handler.Handle(Request(resource), default);

        Assert.Equal(1, harness.UnitOfWork.Executions);
        Assert.Equal(1, harness.Bookings.SaveChangesCount);
        Assert.True(harness.Bookings.SavedAfterCreate);
    }

    // The reads are scoped to the interval asked about, not to the whole day —
    // nothing outside it can change whether the request itself is covered.
    [Fact]
    public async Task ScopesItsReadsToTheRequestedInterval()
    {
        var resource = Room();
        var harness = Build(resource);

        await harness.Handler.Handle(Request(resource), default);

        var expected = new UtcInterval(At(9), At(10));
        Assert.Equal(expected, harness.Availability.BlackoutSpan);
        Assert.Equal(expected, harness.Availability.BookingSpan);
    }

    // ---- Approval routing (FR-7.1) -----------------------------------------

    [Fact]
    public async Task PendsABookingOnAnApprovalGatedResource()
    {
        var resource = Room(requiresApproval: true);
        var harness = Build(resource, approvalExpiryHours: 48);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.Equal(BookingStatus.Pending, response.Status);
        Assert.Equal(BookingStatus.Pending, harness.Bookings.Created!.Status);

        Assert.NotNull(response.Approval);
        Assert.Equal(harness.Bookings.AddedApprovalRequest!.Id, response.Approval!.ApprovalRequestId);
        Assert.Equal(NowUtc.AddHours(48), response.Approval.ExpiresAtUtc);
    }

    // A Pending booking still holds its units — which is why it is safe to create
    // one rather than refuse (decision 0005, and the procedure counts Pending).
    [Fact]
    public async Task ThePendingApprovalRequestIsAnchoredToTheBooking()
    {
        var resource = Room(requiresApproval: true);
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.Equal(response.Id, harness.Bookings.AddedApprovalRequest!.BookingId);
        Assert.Equal(ApprovalDecision.Pending, harness.Bookings.AddedApprovalRequest.Decision);
    }

    // FR-7.4: a tenant that sets no expiry leaves the request pending
    // indefinitely, which is configuration rather than a missing value.
    [Fact]
    public async Task APendingRequestWithNoConfiguredExpiryNeverExpires()
    {
        var resource = Room(requiresApproval: true);
        var harness = Build(resource, approvalExpiryHours: null);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.Null(response.Approval!.ExpiresAtUtc);
        Assert.Null(harness.Bookings.AddedApprovalRequest!.ExpiresAtUtc);
    }

    [Fact]
    public async Task NoApprovalRequestIsCreatedForAnUngatedResource()
    {
        var resource = Room();
        var harness = Build(resource);

        await harness.Handler.Handle(Request(resource), default);

        Assert.Null(harness.Bookings.AddedApprovalRequest);
    }

    // Hardening pass, P1. The resource snapshot read before the unit of work
    // opened said RequiresApproval = false, so the handler asked
    // dbo.CreateBooking for Confirmed — but the procedure re-reads
    // RequiresApproval under its own lock and can answer with Pending instead,
    // if an admin flipped the flag on in the gap. Before this pass the handler
    // trusted its own guess unconditionally: it would have reported Confirmed,
    // enqueued a Confirmed notification, and created no ApprovalRequest at all —
    // a Pending booking (in the database) with no decision record for any
    // approver to find, the exact FR-7.1 invariant this codebase guards
    // everywhere else. Simulated here via FakeBookingRepository's
    // actualStatusOverride, since no fake can simulate the lock itself — the
    // race at the database is CreateBookingProcedureTests' job.
    [Fact]
    public async Task ReactsToTheProcedureDowngradingAConfirmedRequestToPending()
    {
        // Approvers assigned but RequiresApproval left false at the object
        // level, so the handler's own pre-check still guesses Confirmed — the
        // resource in the state it would be in a moment before an admin's
        // SetRequiresApproval(true, ...) commits.
        var resource = Room(requiresApproval: false);
        resource.ReplaceApprovers([ApproverId], ActorId, NowUtc);

        var harness = Build(resource, actualStatusOverride: BookingStatus.Pending, approvalExpiryHours: 48);

        var response = await harness.Handler.Handle(Request(resource), default);

        Assert.Equal(BookingStatus.Pending, response.Status);
        Assert.NotNull(response.Approval);

        Assert.NotNull(harness.Bookings.AddedApprovalRequest);
        Assert.Equal(response.Id, harness.Bookings.AddedApprovalRequest!.BookingId);
        Assert.Equal(NowUtc.AddHours(48), harness.Bookings.AddedApprovalRequest.ExpiresAtUtc);

        // Exactly one notification, and it is the approval-requested kind —
        // never the Confirmed one the handler's own stale guess would have sent.
        var notification = Assert.Single(harness.Bookings.AddedNotifications);
        Assert.Equal(NotificationKind.ApprovalRequested, notification.Kind);
    }

    // ---- Notification rows (FR-8.1) ----------------------------------------

    [Fact]
    public async Task EnqueuesAConfirmationForTheBooker()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource), default);

        var notification = Assert.Single(harness.Bookings.AddedNotifications);
        Assert.Equal(NotificationKind.Confirmed, notification.Kind);
        Assert.Equal(ActorId, notification.RecipientUserId);
        Assert.Equal(response.Id, notification.BookingId);
        Assert.Equal(NowUtc, notification.SendAtUtc);
        Assert.Null(notification.SentAtUtc);
    }

    // A pending booking notifies the approvers, and **not** the booker: there is
    // nothing to confirm yet, and the response has already told them the status.
    // FR-7.3 puts the member's notification at the decision, which is WP-5's.
    [Fact]
    public async Task EnqueuesAnApprovalRequestForEachApproverAndNotTheBooker()
    {
        var resource = Room(requiresApproval: true);
        var harness = Build(resource);

        await harness.Handler.Handle(Request(resource), default);

        var notification = Assert.Single(harness.Bookings.AddedNotifications);
        Assert.Equal(NotificationKind.ApprovalRequested, notification.Kind);
        Assert.Equal(ApproverId, notification.RecipientUserId);
        Assert.DoesNotContain(harness.Bookings.AddedNotifications, n => n.RecipientUserId == ActorId);
    }

    // ---- Rejections the handler decides ------------------------------------

    [Fact]
    public async Task RefusesAnUnknownResource()
    {
        var resource = Room();
        var harness = Build(resource);

        var request = Request(resource) with { ResourceId = Guid.NewGuid() };

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => harness.Handler.Handle(request, default));
    }

    [Fact]
    public async Task RefusesAnArchivedResource()
    {
        var resource = Room(archived: true);
        var harness = Build(resource);

        await Assert.ThrowsAsync<ResourceArchivedException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    [Theory]
    [InlineData(60, null, 30)]  // under the minimum
    [InlineData(null, 60, 120)] // over the maximum
    public async Task RefusesADurationOutsideTheResourceLimits(int? min, int? max, int minutes)
    {
        var resource = Room(minDurationMinutes: min, maxDurationMinutes: max);
        var harness = Build(resource);

        var request = Request(resource) with { EndsAtUtc = At(9).AddMinutes(minutes) };

        await Assert.ThrowsAsync<BookingDurationOutOfRangeException>(
            () => harness.Handler.Handle(request, default));
    }

    // A span longer than the maximum is fine for *availability* — a booker takes
    // a piece of it — but a booking of that length is not. The two questions are
    // CanFitABooking and AllowsBookingDuration, and this asserts the handler asks
    // the second.
    [Fact]
    public async Task AppliesTheMaximumDurationWhichTheAvailabilityQueryDoesNot()
    {
        var resource = Room(maxDurationMinutes: 60);
        var harness = Build(resource);

        Assert.True(resource.CanFitABooking(TimeSpan.FromHours(3)));

        await Assert.ThrowsAsync<BookingDurationOutOfRangeException>(
            () => harness.Handler.Handle(Request(resource, endHour: 12), default));
    }

    // Entirely elapsed. The test is on the end, so this is a booking wholly
    // behind us.
    [Fact]
    public async Task RefusesAnIntervalThatHasAlreadyEnded()
    {
        var resource = Room();
        var harness = Build(resource);

        var request = Request(resource) with
        {
            StartsAtUtc = NowUtc.AddHours(-3),
            EndsAtUtc = NowUtc.AddHours(-2),
        };

        await Assert.ThrowsAsync<BookingInThePastException>(
            () => harness.Handler.Handle(request, default));
    }

    // ...and an interval that merely *started* in the past is accepted — booking
    // the room you are already sitting in (decision 0019's rule, reapplied).
    [Fact]
    public async Task AcceptsAnIntervalThatMerelyStartedInThePast()
    {
        var resource = Room();
        var harness = Build(resource);

        var request = Request(resource) with
        {
            StartsAtUtc = NowUtc.AddHours(-1),
            EndsAtUtc = NowUtc.AddHours(1),
        };

        var response = await harness.Handler.Handle(request, default);

        Assert.Equal(BookingStatus.Confirmed, response.Status);
    }

    [Fact]
    public async Task RefusesAnIntervalOutsideTheSchedule()
    {
        var resource = Room(open: false);
        var harness = Build(resource);

        await Assert.ThrowsAsync<OutsideAvailabilityException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    [Fact]
    public async Task RefusesAnIntervalCoveredByABlackout()
    {
        var resource = Room();
        var harness = Build(resource, blackouts: [new UtcInterval(At(8), At(12))]);

        await Assert.ThrowsAsync<BookingInBlackoutPeriodException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    [Fact]
    public async Task RefusesWhenExistingBookingsHaveTakenEveryUnit()
    {
        var resource = Room(capacity: 1);
        var harness = Build(
            resource,
            bookings: [new BookedQuantity(new UtcInterval(At(9), At(10)), 1)]);

        await Assert.ThrowsAsync<SlotUnavailableException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    // Nothing is written when a rule refuses the request: the procedure is never
    // called at all, so there is nothing to undo.
    [Fact]
    public async Task WritesNothingWhenARuleRefusesTheRequest()
    {
        var resource = Room(open: false);
        var harness = Build(resource);

        await Assert.ThrowsAsync<OutsideAvailabilityException>(
            () => harness.Handler.Handle(Request(resource), default));

        Assert.Null(harness.Bookings.Created);
        Assert.Equal(0, harness.Bookings.SaveChangesCount);
        Assert.Equal(0, harness.UnitOfWork.Executions);
    }

    // ---- Rejections the procedure decides ----------------------------------

    // Every result code the procedure can return maps to a reason code, including
    // the two the handler already checked: a resource can be archived between the
    // check and the insert, and ResourceNotFound is also how 0023's fail-closed
    // guard surfaces when RLS has no session context.
    [Theory]
    [InlineData(BookingCreationResult.ResourceNotFound, typeof(ResourceNotFoundException))]
    [InlineData(BookingCreationResult.ResourceArchived, typeof(ResourceArchivedException))]
    [InlineData(BookingCreationResult.BlackoutPeriod, typeof(BookingInBlackoutPeriodException))]
    [InlineData(BookingCreationResult.SlotUnavailable, typeof(SlotUnavailableException))]
    [InlineData(BookingCreationResult.CapacityExceeded, typeof(CapacityExceededException))]
    public async Task MapsEveryProcedureRejectionOntoItsReasonCode(
        BookingCreationResult result,
        Type expected)
    {
        var resource = Room();
        var harness = Build(resource, result);

        var exception = await Assert.ThrowsAnyAsync<AppException>(
            () => harness.Handler.Handle(Request(resource), default));

        Assert.IsType(expected, exception);
    }

    // The figure the procedure measured under its lock reaches the exception. It
    // is log-only (decision 0016), but it is the difference between a diagnosable
    // 409 and an opaque one.
    [Fact]
    public async Task CarriesTheProceduresRemainingCapacityOntoTheRejection()
    {
        var resource = Room();
        var harness = Build(resource, BookingCreationResult.CapacityExceeded, remainingCapacity: 2);

        var exception = await Assert.ThrowsAsync<CapacityExceededException>(
            () => harness.Handler.Handle(Request(resource, quantity: 3), default));

        Assert.Equal(2, exception.RemainingCapacity);
    }

    // Nothing is saved when the procedure refuses — the throw happens inside the
    // unit of work, so the transaction takes the staged rows with it.
    [Fact]
    public async Task SavesNothingWhenTheProcedureRefuses()
    {
        var resource = Room(capacity: 1);
        var harness = Build(resource, BookingCreationResult.SlotUnavailable);

        await Assert.ThrowsAsync<SlotUnavailableException>(
            () => harness.Handler.Handle(Request(resource), default));

        Assert.Equal(1, harness.UnitOfWork.Executions);
        Assert.Equal(0, harness.Bookings.SaveChangesCount);
    }

    // ---- The order of the rejections ---------------------------------------

    // All of them wrong at once. The order is fixed so a client's handling of a
    // given request never changes for reasons it cannot see, and the most
    // structural answer wins.
    [Fact]
    public async Task ArchivedIsReportedAheadOfEveryOtherRule()
    {
        var resource = Room(capacity: 1, minDurationMinutes: 600, archived: true, open: false);
        var harness = Build(
            resource,
            blackouts: [new UtcInterval(At(8), At(12))],
            bookings: [new BookedQuantity(new UtcInterval(At(9), At(10)), 1)]);

        await Assert.ThrowsAsync<ResourceArchivedException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    [Fact]
    public async Task DurationIsReportedAheadOfAvailability()
    {
        var resource = Room(minDurationMinutes: 600, open: false);
        var harness = Build(resource);

        await Assert.ThrowsAsync<BookingDurationOutOfRangeException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    [Fact]
    public async Task AvailabilityIsReportedAheadOfBlackoutAndCapacity()
    {
        var resource = Room(capacity: 1, open: false);
        var harness = Build(
            resource,
            blackouts: [new UtcInterval(At(8), At(12))],
            bookings: [new BookedQuantity(new UtcInterval(At(9), At(10)), 1)]);

        await Assert.ThrowsAsync<OutsideAvailabilityException>(
            () => harness.Handler.Handle(Request(resource), default));
    }

    // ---- Wiring ------------------------------------------------------------

    // A 500, not a reason code: the endpoint sits behind a policy requiring a
    // tenant principal, so a missing user id means broken wiring rather than
    // anything a client did.
    [Fact]
    public async Task ThrowsWhenThereIsNoAuthenticatedUser()
    {
        var resource = Room();
        var availability = new FakeAvailabilityRepository(resource);

        var handler = new CreateBookingCommandRequestHandler(
            availability,
            new FakeBookingRepository(),
            new FakeTimeZoneCatalog("UTC"),
            new PassThroughUnitOfWork(),
            new FixedCurrentUser(null),
            new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.Handle(Request(resource), default));
    }
}
