using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.BlackoutPeriods.DeleteBlackoutPeriod;
using BookSpace.Application.Features.BlackoutPeriods.UpdateBlackoutPeriod;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.BlackoutPeriods;

// WP-3 Phase 4 step 2, FR-3.4: the edit and the delete. Both in one file because
// they share the resource/blackout lookup ladder and most of their rejections.
public class UpdateAndDeleteBlackoutPeriodHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly DateTime CreatedUtc = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowUtc = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime OriginalStarts = new(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OriginalEnds = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

    private static Resource ExistingResource() =>
        new(Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, 8, "America/New_York",
            requiresApproval: false, minDurationMinutes: 30, maxDurationMinutes: 240,
            description: "Main room", createdByUserId: ActorId, nowUtc: CreatedUtc);

    private static BlackoutPeriod ExistingBlackout(Resource resource) =>
        new(Guid.NewGuid(), OrgId, resource.Id, OriginalStarts, OriginalEnds,
            "Boiler service", ActorId, CreatedUtc);

    private static Booking BookingOn(Resource resource, DateTime startsAtUtc, DateTime endsAtUtc) =>
        new(Guid.NewGuid(), OrgId, resource.Id, OwnerId, null,
            startsAtUtc, endsAtUtc, 1, "Team sync", BookingStatus.Confirmed, OwnerId, CreatedUtc);

    private static UpdateBlackoutPeriodCommandRequestHandler UpdateHandler(
        FakeBlackoutPeriodRepository repository) =>
        new(repository, new FixedCurrentUser(AdminId), new TestClock(NowUtc));

    private static DeleteBlackoutPeriodCommandRequestHandler DeleteHandler(
        FakeBlackoutPeriodRepository repository) =>
        new(repository);

    private static UpdateBlackoutPeriodCommandRequest UpdateRequest(
        Guid resourceId,
        Guid blackoutId,
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null,
        string? reason = "Boiler service") =>
        new(resourceId, blackoutId, startsAtUtc ?? OriginalStarts, endsAtUtc ?? OriginalEnds, reason);

    // ---- Update: the happy path ----

    [Fact]
    public async Task Update_RevisesTheIntervalAndTouchesTheAuditColumns()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        var newStarts = OriginalStarts.AddHours(2);
        var newEnds = OriginalEnds.AddHours(6);

        var response = await UpdateHandler(repository).Handle(
            UpdateRequest(resource.Id, blackout.Id, newStarts, newEnds, "Deep clean"),
            CancellationToken.None);

        Assert.Equal(blackout.Id, response.Id);
        Assert.Equal(newStarts, response.StartsAtUtc);
        Assert.Equal(newEnds, response.EndsAtUtc);
        Assert.Equal("Deep clean", response.Reason);
        Assert.Equal(CreatedUtc, response.CreatedAtUtc);
        Assert.Equal(NowUtc, response.UpdatedAtUtc);
        Assert.Equal(AdminId, blackout.UpdatedByUserId);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    // A full representation, so an omitted reason clears it.
    [Fact]
    public async Task Update_ClearsTheReasonWhenTheRequestOmitsIt()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        var response = await UpdateHandler(repository).Handle(
            UpdateRequest(resource.Id, blackout.Id, reason: null), CancellationToken.None);

        Assert.Null(response.Reason);
        Assert.Null(blackout.Reason);
    }

    // ---- Update: decision 0001 re-runs over the NEW interval ----

    // The case decision 0001 names explicitly. The widened range reaches a
    // booking the original never touched.
    [Fact]
    public async Task Update_CancelsBookingsTheWidenedIntervalNowCovers()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var newlyCovered = BookingOn(resource, OriginalEnds.AddHours(1), OriginalEnds.AddHours(2));
        var repository = new FakeBlackoutPeriodRepository(resource, [newlyCovered], blackout);

        var response = await UpdateHandler(repository).Handle(
            UpdateRequest(resource.Id, blackout.Id, OriginalStarts, OriginalEnds.AddHours(6)),
            CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, newlyCovered.Status);
        Assert.Equal(newlyCovered.Id, Assert.Single(response.CancelledBookings).BookingId);
        Assert.Single(repository.AddedNotifications);
    }

    // The cascade window is the *new* interval, not the old one — which is what
    // makes moving a blackout work rather than only widening it.
    [Fact]
    public async Task Update_AsksForOverlapsAcrossTheNewIntervalNotTheOld()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        var movedStarts = OriginalStarts.AddDays(1);
        var movedEnds = OriginalEnds.AddDays(1);

        await UpdateHandler(repository).Handle(
            UpdateRequest(resource.Id, blackout.Id, movedStarts, movedEnds), CancellationToken.None);

        Assert.Equal((resource.Id, movedStarts, movedEnds, NowUtc), repository.CascadeQuery);
    }

    // Forwards only. Nothing is restored when a blackout is narrowed, because a
    // cancellation is irreversible — the repository is asked only about the new
    // interval, so there is no code path that could un-cancel anything.
    [Fact]
    public async Task Update_RestoresNothingWhenTheIntervalIsNarrowed()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);

        // Already cancelled by the original interval, and now outside it.
        var previouslyCancelled = BookingOn(resource, OriginalStarts, OriginalStarts.AddHours(1));
        previouslyCancelled.CancelForBlackout("Blackout: boiler service", CreatedUtc);

        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        var response = await UpdateHandler(repository).Handle(
            UpdateRequest(resource.Id, blackout.Id, OriginalStarts.AddHours(3), OriginalEnds),
            CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, previouslyCancelled.Status);
        Assert.Empty(response.CancelledBookings);
    }

    // ---- Update: rejections ----

    [Fact]
    public async Task Update_ThrowsResourceNotFoundForAnUnknownOrCrossTenantResource()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        var exception = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            UpdateHandler(repository).Handle(
                UpdateRequest(Guid.NewGuid(), blackout.Id), CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceNotFound, exception.ReasonCode);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // The second 404, and the reason it is a distinct code: the resource is fine,
    // the blackout is not.
    [Fact]
    public async Task Update_ThrowsBlackoutPeriodNotFoundForAnUnknownBlackout()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource, existing: ExistingBlackout(resource));

        var exception = await Assert.ThrowsAsync<BlackoutPeriodNotFoundException>(() =>
            UpdateHandler(repository).Handle(
                UpdateRequest(resource.Id, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(ReasonCodes.BlackoutPeriodNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // A real blackout reached through the wrong resource is a 404, not an edit
    // applied to the wrong room.
    [Fact]
    public async Task Update_ThrowsBlackoutPeriodNotFoundForABlackoutOnAnotherResource()
    {
        var resource = ExistingResource();
        var otherResource = ExistingResource();
        var blackoutOnOther = ExistingBlackout(otherResource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackoutOnOther);

        await Assert.ThrowsAsync<BlackoutPeriodNotFoundException>(() =>
            UpdateHandler(repository).Handle(
                UpdateRequest(resource.Id, blackoutOnOther.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Update_ThrowsResourceArchivedForAnArchivedResource()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        resource.Archive(AdminId, NowUtc);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        var exception = await Assert.ThrowsAsync<ResourceArchivedException>(() =>
            UpdateHandler(repository).Handle(
                UpdateRequest(resource.Id, blackout.Id), CancellationToken.None));

        Assert.Equal(ReasonCodes.ResourceArchived, exception.ReasonCode);
    }

    // Moving a blackout entirely into the past is refused, for the same reason
    // creating one there is.
    [Fact]
    public async Task Update_ThrowsBlackoutPeriodElapsedWhenMovedEntirelyIntoThePast()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        var exception = await Assert.ThrowsAsync<BlackoutPeriodElapsedException>(() =>
            UpdateHandler(repository).Handle(
                UpdateRequest(resource.Id, blackout.Id, NowUtc.AddHours(-5), NowUtc.AddHours(-3)),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.BlackoutPeriodElapsed, exception.ReasonCode);

        // And the entity was not touched before the rule ran.
        Assert.Equal(OriginalStarts, blackout.StartsAtUtc);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // The check is on the new interval only: a blackout created last week for
    // yesterday is allowed to be edited into the future.
    [Fact]
    public async Task Update_AllowsRevivingAnAlreadyElapsedBlackoutIntoTheFuture()
    {
        var resource = ExistingResource();
        var elapsed = new BlackoutPeriod(
            Guid.NewGuid(), OrgId, resource.Id,
            NowUtc.AddDays(-3), NowUtc.AddDays(-2), "Old", ActorId, CreatedUtc);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: elapsed);

        var response = await UpdateHandler(repository).Handle(
            UpdateRequest(resource.Id, elapsed.Id, NowUtc.AddDays(1), NowUtc.AddDays(2)),
            CancellationToken.None);

        Assert.Equal(NowUtc.AddDays(1), response.StartsAtUtc);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    [Fact]
    public async Task Update_ThrowsInvalidOperationWhenThereIsNoAuthenticatedUser()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);
        var handler = new UpdateBlackoutPeriodCommandRequestHandler(
            repository, new FixedCurrentUser(null), new TestClock(NowUtc));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(UpdateRequest(resource.Id, blackout.Id), CancellationToken.None));
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_RemovesTheBlackout()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        await DeleteHandler(repository).Handle(
            new DeleteBlackoutPeriodCommandRequest(resource.Id, blackout.Id), CancellationToken.None);

        Assert.Same(blackout, repository.Removed);
        Assert.Equal(1, repository.SaveChangesCount);
    }

    // Deleting a blackout stops it blocking future bookings; it never means
    // "undo". A cancellation is irreversible.
    [Fact]
    public async Task Delete_LeavesAlreadyCancelledBookingsCancelled()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        var cancelled = BookingOn(resource, OriginalStarts, OriginalStarts.AddHours(1));
        cancelled.CancelForBlackout("Blackout: boiler service", CreatedUtc);
        var repository = new FakeBlackoutPeriodRepository(resource, [cancelled], blackout);

        await DeleteHandler(repository).Handle(
            new DeleteBlackoutPeriodCommandRequest(resource.Id, blackout.Id), CancellationToken.None);

        Assert.Equal(BookingStatus.Cancelled, cancelled.Status);
        Assert.Empty(repository.AddedNotifications);
    }

    [Fact]
    public async Task Delete_ThrowsResourceNotFoundForAnUnknownOrCrossTenantResource()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource, existing: ExistingBlackout(resource));

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            DeleteHandler(repository).Handle(
                new DeleteBlackoutPeriodCommandRequest(Guid.NewGuid(), Guid.NewGuid()),
                CancellationToken.None));

        Assert.Null(repository.Removed);
    }

    // Deliberately not idempotent: a second DELETE is a 404. This endpoint cannot
    // tell "already deleted" from "another tenant's id" (AC-4), so a blanket 204
    // would silently accept the latter.
    [Fact]
    public async Task Delete_ThrowsBlackoutPeriodNotFoundForAnUnknownBlackout()
    {
        var resource = ExistingResource();
        var repository = new FakeBlackoutPeriodRepository(resource, existing: ExistingBlackout(resource));

        var exception = await Assert.ThrowsAsync<BlackoutPeriodNotFoundException>(() =>
            DeleteHandler(repository).Handle(
                new DeleteBlackoutPeriodCommandRequest(resource.Id, Guid.NewGuid()),
                CancellationToken.None));

        Assert.Equal(ReasonCodes.BlackoutPeriodNotFound, exception.ReasonCode);
        Assert.Null(repository.Removed);
        Assert.Equal(0, repository.SaveChangesCount);
    }

    // Consistency over convenience: "an archived resource accepts no writes" is a
    // rule an admin can hold in their head, and its blackouts block nothing
    // anyway.
    [Fact]
    public async Task Delete_ThrowsResourceArchivedForAnArchivedResource()
    {
        var resource = ExistingResource();
        var blackout = ExistingBlackout(resource);
        resource.Archive(AdminId, NowUtc);
        var repository = new FakeBlackoutPeriodRepository(resource, existing: blackout);

        await Assert.ThrowsAsync<ResourceArchivedException>(() =>
            DeleteHandler(repository).Handle(
                new DeleteBlackoutPeriodCommandRequest(resource.Id, blackout.Id),
                CancellationToken.None));

        Assert.Null(repository.Removed);
    }
}
