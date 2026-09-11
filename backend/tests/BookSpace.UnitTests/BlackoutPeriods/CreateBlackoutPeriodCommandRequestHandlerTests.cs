using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.BlackoutPeriods.CreateBlackoutPeriod;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Bookings;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.BlackoutPeriods;

// WP-3 Phase 4 step 1, FR-3.4 plus decision 0001's cascade.
public class CreateBlackoutPeriodCommandRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly DateTime CreatedUtc = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowUtc = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    // Tomorrow, so the blackout is comfortably in the future.
    private static readonly DateTime BlackoutStarts = new(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BlackoutEnds = new(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);

    private static Resource ExistingResource() =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, 8, "America/New_York",
            requiresApproval: false, minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main room", createdByUserId: ActorId, nowUtc: CreatedUtc);

    private static Booking BookingOn(
        Resource resource,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        BookingStatus status = BookingStatus.Confirmed,
        Guid? recurrenceRuleId = null) =>
        new(Guid.NewGuid(), OrgId, resource.Id, OwnerId, recurrenceRuleId,
            startsAtUtc, endsAtUtc, 1, "Team sync", status, OwnerId, CreatedUtc);

    private static CreateBlackoutPeriodCommandRequestHandler Handler(
        FakeBlackoutPeriodRepository repository) =>
        new(repository, new PassThroughUnitOfWork(), new FixedCurrentUser(AdminId), new TestClock(NowUtc));

    private static CreateBlackoutPeriodCommandRequest Request(
        Guid resourceId,
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null,
        string? reason = "Boiler service") =>
        new(resourceId, startsAtUtc ?? BlackoutStarts, endsAtUtc ?? BlackoutEnds, reason);

    // ---- The happy path ----

    [Fact]
    public async Task Handle_StoresTheBlackoutAgainstTheResourcesOwnTenant()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);

        var response = await Handler(repository).Handle(
            Request(resource.Id), CancellationToken.None);

        Assert.Equal(resource.Id, response.ResourceId);
        Assert.Equal(BlackoutStarts, response.StartsAtUtc);
        Assert.Equal(BlackoutEnds, response.EndsAtUtc);
        Assert.Equal("Boiler service", response.Reason);
        Assert.Equal(NowUtc, response.CreatedAtUtc);
        Assert.NotEqual(Guid.Empty, response.Id);

        // OrgId comes from the loaded resource, never the request — the composite
        // FK and the SaveChanges guard both depend on it matching.
        Assert.NotNull(repository.Added);
        Assert.Equal(OrgId, repository.Added!.OrgId);
        Assert.Equal(AdminId, repository.Added.CreatedByUserId);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    // A blackout that hits nothing still reports the cascade ran.
    [Fact]
    public async Task Handle_ReturnsAnEmptyCancellationListWhenNothingOverlaps()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);

        var response = await Handler(repository).Handle(
            Request(resource.Id), CancellationToken.None);

        Assert.Empty(response.CancelledBookings);
        Assert.Empty(repository.AddedNotifications);
    }

    // The cascade window is the blackout's own interval, and the clock is passed
    // through so the repository can apply the same "not already finished" test
    // the domain enforces.
    [Fact]
    public async Task Handle_AsksForOverlapsAcrossExactlyTheBlackoutsInterval()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);

        await Handler(repository).Handle(Request(resource.Id), CancellationToken.None);

        Assert.Equal(
            (resource.Id, BlackoutStarts, BlackoutEnds, NowUtc),
            repository.CascadeQuery);
    }

    // Hardening pass, P2: "a booking that is no longer Pending cannot have an
    // actionable Pending ApprovalRequest", enforced here for the blackout
    // cascade specifically — the one cancellation path that cancels bookings
    // it did not look up individually, so it needs its own proof.
    [Fact]
    public async Task Handle_WithdrawsThePendingApprovalRequestOfEveryPendingBookingItCancels()
    {
        var resource = ExistingResource();
        var confirmed = BookingOn(resource, BlackoutStarts.AddHours(1), BlackoutStarts.AddHours(2));
        var pending = BookingOn(
            resource, BlackoutStarts.AddHours(3), BlackoutStarts.AddHours(4), BookingStatus.Pending);
        var approval = new ApprovalRequest(Guid.NewGuid(), pending.Id, NowUtc, null);
        var repository = new FakeBlackoutPeriodRepository(resource, [confirmed, pending])
        {
            PendingApprovalRequests = [approval],
        };

        await Handler(repository).Handle(Request(resource.Id), CancellationToken.None);

        Assert.Equal(ApprovalDecision.Withdrawn, approval.Decision);
        Assert.Equal(NowUtc, approval.DecidedAtUtc);
    }

    // ---- Decision 0001: absolute priority ----

    [Fact]
    public async Task Handle_CancelsEveryOverlappingBookingAndNotifiesItsOwner()
    {
        var resource = ExistingResource();
        var confirmed = BookingOn(resource, BlackoutStarts.AddHours(1), BlackoutStarts.AddHours(2));
        var pending = BookingOn(
            resource, BlackoutStarts.AddHours(3), BlackoutStarts.AddHours(4), BookingStatus.Pending);
        var repository = new FakeBlackoutPeriodRepository(resource, [confirmed, pending]);

        var response = await Handler(repository).Handle(
            Request(resource.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, confirmed.Status);
        Assert.Equal(BookingStatus.Cancelled, pending.Status);
        Assert.Equal(2, response.CancelledBookings.Count);

        // One notification per cancellation, to the booking's owner, due
        // immediately. The dispatch job does not exist yet (CLAUDE.md §7) — the
        // row is the deliverable.
        Assert.Equal(2, repository.AddedNotifications.Count);
        Assert.All(repository.AddedNotifications, n =>
        {
            Assert.Equal(NotificationKind.Cancelled, n.Kind);
            Assert.Equal(OwnerId, n.RecipientUserId);
            Assert.Equal(NowUtc, n.SendAtUtc);
            Assert.Null(n.SentAtUtc);

            // Anchored to the booking, not decision 0008's dual anchor: every
            // booking here exists.
            Assert.NotNull(n.BookingId);
            Assert.Null(n.RecurrenceRuleId);

            // A person did cause this, unlike the no-show job's rows.
            Assert.Equal(AdminId, n.CreatedByUserId);
        });

        Assert.Equal(
            new[] { confirmed.Id, pending.Id }.Order(),
            repository.AddedNotifications.Select(n => n.BookingId!.Value).Order());
    }

    // The cancellation reason is a text snapshot rather than a foreign key, so it
    // still says why after the blackout row is hard-deleted.
    [Fact]
    public async Task Handle_RecordsTheBlackoutAndItsReasonOnEachCancellation()
    {
        var resource = ExistingResource();
        var booking = BookingOn(resource, BlackoutStarts.AddHours(1), BlackoutStarts.AddHours(2));
        var repository = new FakeBlackoutPeriodRepository(resource, [booking]);

        await Handler(repository).Handle(
            Request(resource.Id, reason: "Boiler service"), CancellationToken.None);

        Assert.Contains("Boiler service", booking.CancellationReason);
        Assert.Contains(repository.Added!.Id.ToString(), booking.CancellationReason);
    }

    // A blackout with no stated reason is legal, so the cancellation text has to
    // stand without one.
    [Fact]
    public async Task Handle_StillRecordsTheBlackoutWhenNoReasonWasGiven()
    {
        var resource = ExistingResource();
        var booking = BookingOn(resource, BlackoutStarts.AddHours(1), BlackoutStarts.AddHours(2));
        var repository = new FakeBlackoutPeriodRepository(resource, [booking]);

        await Handler(repository).Handle(
            Request(resource.Id, reason: null), CancellationToken.None);

        Assert.Contains(repository.Added!.Id.ToString(), booking.CancellationReason);
    }

    // Decision 0001 cancels the occurrences, never the rule. The response says
    // which cancellations were occurrences so an admin can see they have punched
    // a hole in a series (PRD AC-2).
    [Fact]
    public async Task Handle_ReportsWhichCancellationsWereSeriesOccurrences()
    {
        var resource = ExistingResource();
        var ruleId = Guid.NewGuid();
        var occurrence = BookingOn(
            resource, BlackoutStarts.AddHours(1), BlackoutStarts.AddHours(2),
            recurrenceRuleId: ruleId);
        var oneOff = BookingOn(resource, BlackoutStarts.AddHours(3), BlackoutStarts.AddHours(4));
        var repository = new FakeBlackoutPeriodRepository(resource, [occurrence, oneOff]);

        var response = await Handler(repository).Handle(
            Request(resource.Id), CancellationToken.None);

        Assert.Equal(
            ruleId,
            response.CancelledBookings.Single(b => b.BookingId == occurrence.Id).RecurrenceRuleId);
        Assert.Null(
            response.CancelledBookings.Single(b => b.BookingId == oneOff.Id).RecurrenceRuleId);
    }

    // The summary reports the booking as it was, so an admin can see what they
    // just cancelled rather than a row full of cancellation metadata.
    [Fact]
    public async Task Handle_ReportsEachCancelledBookingsOriginalInterval()
    {
        var resource = ExistingResource();
        var starts = BlackoutStarts.AddHours(1);
        var ends = BlackoutStarts.AddHours(2);
        var booking = BookingOn(resource, starts, ends);
        var repository = new FakeBlackoutPeriodRepository(resource, [booking]);

        var response = await Handler(repository).Handle(
            Request(resource.Id), CancellationToken.None);

        var summary = Assert.Single(response.CancelledBookings);
        Assert.Equal(booking.Id, summary.BookingId);
        Assert.Equal(OwnerId, summary.UserId);
        Assert.Equal(starts, summary.StartsAtUtc);
        Assert.Equal(ends, summary.EndsAtUtc);
    }

    // Everything lands in one save, so a cancellation can never commit without
    // its blackout or its notification.
    [Fact]
    public async Task Handle_SavesTheBlackoutCancellationsAndNotificationsTogether()
    {
        var resource = ExistingResource();
        var booking = BookingOn(resource, BlackoutStarts.AddHours(1), BlackoutStarts.AddHours(2));
        var repository = new FakeBlackoutPeriodRepository(resource, [booking]);

        await Handler(repository).Handle(Request(resource.Id), CancellationToken.None);

        Assert.Equal(1, repository.SaveChangesCount);
    }

    // ---- Rejections ----

    // AC-4: another tenant's real id and an id that exists nowhere are the same
    // answer, because the tenant-filtered repository returns null for both.
    [Fact]
    public async Task Handle_ThrowsResourceNotFoundForAnUnknownOrCrossTenantResource()
    {
        var repository = new FakeBlackoutPeriodRepository(ExistingResource());

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Handler(repository).Handle(Request(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceNotFound, exception.ReasonCode);
        Assert.Null(repository.Added);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // FR-3.5: an archived resource takes no new bookings, so blocking time on it
    // describes a restriction on something already fully restricted.
    [Fact]
    public async Task Handle_ThrowsResourceArchivedForAnArchivedResource()
    {
        var resource = ExistingResource();
        resource.Archive(AdminId, NowUtc);
        var repository = new FakeBlackoutPeriodRepository(resource);

        var exception = await Assert.ThrowsAsync<ResourceArchivedException>(() =>
            Handler(repository).Handle(Request(resource.Id), CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceArchived, exception.ReasonCode);
        Assert.Null(repository.Added);
    }

    // Owner's call, 2026-09-02: a fully elapsed blackout blocks nothing, and its
    // only reachable effect would be reaching backwards into history.
    [Fact]
    public async Task Handle_ThrowsBlackoutPeriodElapsedForAnIntervalEntirelyInThePast()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);

        var exception = await Assert.ThrowsAsync<BlackoutPeriodElapsedException>(() =>
            Handler(repository).Handle(
                Request(resource.Id, NowUtc.AddHours(-5), NowUtc.AddHours(-3)),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.BlackoutPeriodElapsed, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
        Assert.Null(repository.Added);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // The boundary: ending exactly now is over.
    [Fact]
    public async Task Handle_ThrowsBlackoutPeriodElapsedForAnIntervalEndingExactlyNow()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);

        await Assert.ThrowsAsync<BlackoutPeriodElapsedException>(() =>
            Handler(repository).Handle(
                Request(resource.Id, NowUtc.AddHours(-2), NowUtc),
                CancellationToken.None));
    }

    // Deliberately NOT refused: "the room flooded this morning and is unusable
    // until Friday" is the ordinary case, and a StartsAtUtc >= now rule would
    // also reject a request assembled a few seconds ago over clock skew.
    [Fact]
    public async Task Handle_AcceptsABlackoutThatStartedInThePastAndRunsIntoTheFuture()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);

        var response = await Handler(repository).Handle(
            Request(resource.Id, NowUtc.AddHours(-4), NowUtc.AddHours(4)),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, response.Id);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    // ---- Zone normalization ----

    // The validator refuses Unspecified, so an offset is the only non-UTC value
    // that reaches the handler. It has to be converted, not stored as-is:
    // datetime2(0) carries no offset, so an unconverted value would black out the
    // wrong hours.
    [Fact]
    public async Task Handle_NormalizesAnOffsetInstantToUtcBeforeStoringIt()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);

        // The same instant as BlackoutStarts, expressed with DateTimeKind.Local —
        // which is what System.Text.Json produces for a payload carrying an
        // explicit offset. Derived from the UTC value rather than hard-coded, so
        // the assertion holds on a machine in any timezone.
        var asLocal = BlackoutStarts.ToLocalTime();
        Assert.Equal(DateTimeKind.Local, asLocal.Kind);

        var response = await Handler(repository).Handle(
            Request(resource.Id, asLocal, BlackoutEnds), CancellationToken.None);

        Assert.Equal(DateTimeKind.Utc, response.StartsAtUtc.Kind);
        Assert.Equal(BlackoutStarts, response.StartsAtUtc);
    }

    // ---- Wiring ----

    // The endpoint sits behind the TenantAdmin policy, so a missing principal is
    // broken wiring rather than a client error — a 500, not a reason code.
    [Fact]
    public async Task Handle_ThrowsInvalidOperationWhenThereIsNoAuthenticatedUser()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource);
        var handler = new CreateBlackoutPeriodCommandRequestHandler(
            repository, new PassThroughUnitOfWork(), new FixedCurrentUser(null), new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(Request(resource.Id), CancellationToken.None));
    }
}
