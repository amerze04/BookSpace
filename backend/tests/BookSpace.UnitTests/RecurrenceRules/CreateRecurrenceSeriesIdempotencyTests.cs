using BookSpace.Application.Abstractions;
using BookSpace.Application.Features.RecurrenceRules;
using BookSpace.Application.Features.RecurrenceRules.CreateSeries;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Bookings;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;

namespace BookSpace.UnitTests.RecurrenceRules;

// Hardening pass, item 11: FR-5.1's series creation gained request-level
// idempotency via a client-supplied key. What a fake can prove: the handler
// resolves the same key to the same rule rather than minting a second one,
// and computes the same booking id for the same (rule, occurrence date)
// every time it is asked. What it cannot prove — that a real retry does not
// insert a second row for an occurrence already committed — needs
// BookingRepository's actual PK-violation read-back against a real SQL
// Server; see BookingProcedureResourceLockTests' sibling integration
// coverage for that half.
public class CreateRecurrenceSeriesIdempotencyTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2027, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly StartDate = new(2027, 3, 8); // a Monday

    private static Resource Room()
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Conference Room A", ResourceType.Room, capacity: 4,
            timeZoneId: "UTC", requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);

        resource.ReplaceAvailabilityWindows(
            Enum.GetValues<DayOfWeek>().Select(day => new AvailabilityWindowDefinition(
                Guid.NewGuid(), day, new TimeOnly(0, 0), new TimeOnly(23, 59, 59))),
            ActorId,
            NowUtc);

        return resource;
    }

    private static CreateRecurrenceSeriesCommandRequest Request(Resource resource, string? idempotencyKey = null) =>
        new(
            resource.Id,
            RecurrenceFrequency.Weekly,
            IntervalValue: 1,
            new TimeOnly(9, 0),
            new TimeOnly(10, 0),
            StartDate,
            EndDate: null,
            OccurrenceCount: 3,
            Quantity: 1,
            Title: "Standup",
            IdempotencyKey: idempotencyKey);

    private sealed record Harness(
        CreateRecurrenceSeriesCommandRequestHandler Handler,
        FakeSeriesBookingRepository Bookings,
        FakeRecurrenceRuleRepository RecurrenceRules);

    private static Harness Build(Resource resource)
    {
        var availability = new FakeAvailabilityRepository(resource, blackouts: null, bookings: null);
        var bookingRepository = new FakeSeriesBookingRepository();
        var recurrenceRules = new FakeRecurrenceRuleRepository();

        var handler = new CreateRecurrenceSeriesCommandRequestHandler(
            availability,
            bookingRepository,
            recurrenceRules,
            new FakeTimeZoneCatalog("UTC"),
            new PassThroughUnitOfWork(),
            new FixedCurrentUser(ActorId),
            new TestClock(NowUtc));

        return new Harness(handler, bookingRepository, recurrenceRules);
    }

    [Fact]
    public async Task AFirstAttemptWithAKeyRecordsAnOperationPointingAtTheNewRule()
    {
        var resource = Room();
        var harness = Build(resource);

        var response = await harness.Handler.Handle(Request(resource, "key-1"), default);

        Assert.NotNull(harness.RecurrenceRules.AddedOperation);
        Assert.Equal("key-1", harness.RecurrenceRules.AddedOperation!.IdempotencyKey);
        Assert.Equal(response.RecurrenceRuleId, harness.RecurrenceRules.AddedOperation.RecurrenceRuleId);
        Assert.Equal(RecurrenceCreationOperationStatus.Active, harness.RecurrenceRules.AddedOperation.Status);
    }

    [Fact]
    public async Task ResumingAnInFlightOperationReusesTheSameRuleInsteadOfMintingANewOne()
    {
        var resource = Room();
        var harness = Build(resource);

        // Simulate "a previous attempt crashed after persisting the rule and
        // the operation row, before any occurrence committed" — exactly the
        // state RemoveOrphanedRuleAsync's catch block deliberately leaves
        // behind (Creating, not Failed) for a genuinely unexpected exception.
        var existingRule = new RecurrenceRule(
            Guid.NewGuid(), resource.OrgId, resource.Id, ActorId, RecurrenceFrequency.Weekly, 1,
            new TimeOnly(9, 0), new TimeOnly(10, 0), StartDate, null, 3, resource.TimeZoneId, ActorId, NowUtc);
        var existingOperation = new RecurrenceCreationOperation(
            Guid.NewGuid(), resource.OrgId, ActorId, "key-1", existingRule.Id, NowUtc);

        harness.RecurrenceRules.ExistingOperation = existingOperation;
        harness.RecurrenceRules.ExistingRule = existingRule;

        var response = await harness.Handler.Handle(Request(resource, "key-1"), default);

        Assert.Equal(existingRule.Id, response.RecurrenceRuleId);
        // No second rule was minted and staged for insertion.
        Assert.Null(harness.RecurrenceRules.Added);
    }

    [Fact]
    public async Task TheSameOccurrenceAlwaysComputesTheSameBookingIdForTheSameRule()
    {
        var resource = Room();

        var firstHarness = Build(resource);
        var firstResponse = await firstHarness.Handler.Handle(Request(resource, "key-1"), default);

        // A second, independent handler instance (standing in for a second
        // process after a crash) resuming against the same rule.
        var secondHarness = Build(resource);
        var reusedRule = new RecurrenceRule(
            firstResponse.RecurrenceRuleId, resource.OrgId, resource.Id, ActorId, RecurrenceFrequency.Weekly, 1,
            new TimeOnly(9, 0), new TimeOnly(10, 0), StartDate, null, 3, resource.TimeZoneId, ActorId, NowUtc);
        secondHarness.RecurrenceRules.ExistingOperation = new RecurrenceCreationOperation(
            Guid.NewGuid(), resource.OrgId, ActorId, "key-1", reusedRule.Id, NowUtc);
        secondHarness.RecurrenceRules.ExistingRule = reusedRule;

        var secondResponse = await secondHarness.Handler.Handle(Request(resource, "key-1"), default);

        Assert.Equal(
            firstResponse.Occurrences.Select(o => o.BookingId),
            secondResponse.Occurrences.Select(o => o.BookingId));
    }

    [Fact]
    public async Task ARetryOfAFailedOperationStartsOverWithAFreshRule()
    {
        var resource = Room();
        var harness = Build(resource);

        // The one prior attempt against this key reserved nothing and was
        // compensated away: Failed, RecurrenceRuleId cleared.
        var failedOperation = new RecurrenceCreationOperation(
            Guid.NewGuid(), resource.OrgId, ActorId, "key-1", Guid.NewGuid(), NowUtc);
        failedOperation.MarkFailed(NowUtc);
        harness.RecurrenceRules.ExistingOperation = failedOperation;

        var response = await harness.Handler.Handle(Request(resource, "key-1"), default);

        Assert.NotNull(harness.RecurrenceRules.Added);
        Assert.Equal(response.RecurrenceRuleId, harness.RecurrenceRules.Added!.Id);
        Assert.Equal(RecurrenceCreationOperationStatus.Active, failedOperation.Status);
    }

    [Fact]
    public async Task WithNoKeyAtAllNoOperationIsEverRecorded()
    {
        var resource = Room();
        var harness = Build(resource);

        await harness.Handler.Handle(Request(resource, idempotencyKey: null), default);

        Assert.Null(harness.RecurrenceRules.AddedOperation);
    }

    // Bug fix (found while verifying the hardening pass, not part of it): two
    // literally-simultaneous first-time requests for the same key both see
    // "never seen before" and both try to insert a RecurrenceCreationOperation
    // — only one wins UQ_RecurrenceCreationOperations_Org_User_Key. The loser
    // must detach its own (never-persisted) rule and operation and resume the
    // winner's row instead of letting the unique violation surface.
    [Fact]
    public async Task LosingTheInsertRaceDetachesItsOwnAttemptAndResumesTheWinner()
    {
        var resource = Room();
        var harness = Build(resource);

        var winnerRule = new RecurrenceRule(
            Guid.NewGuid(), resource.OrgId, resource.Id, ActorId, RecurrenceFrequency.Weekly, 1,
            new TimeOnly(9, 0), new TimeOnly(10, 0), StartDate, null, 3, resource.TimeZoneId, ActorId, NowUtc);
        var winnerOperation = new RecurrenceCreationOperation(
            Guid.NewGuid(), resource.OrgId, ActorId, "key-1", winnerRule.Id, NowUtc);
        winnerOperation.MarkActive(NowUtc);

        harness.RecurrenceRules.ExistingOperation = winnerOperation;
        harness.RecurrenceRules.ExistingRule = winnerRule;
        harness.RecurrenceRules.SaveNewOperationSucceeds = false;

        var response = await harness.Handler.Handle(Request(resource, "key-1"), default);

        Assert.Equal(winnerRule.Id, response.RecurrenceRuleId);
        // This attempt's own losing rule and operation were detached, not
        // left tracked as a phantom insert for a later SaveChanges to retry.
        Assert.NotNull(harness.RecurrenceRules.Removed);
        Assert.NotEqual(winnerRule.Id, harness.RecurrenceRules.Removed!.Id);
        Assert.NotNull(harness.RecurrenceRules.RemovedOperation);
    }

    // Bug fix (found while verifying the hardening pass, not part of it): an
    // idempotency key is scoped to (OrgId, UserId), not to a resource — a key
    // already bound to one resource's rule must not be resumed against a
    // request for a *different* resource, which would otherwise create
    // Bookings whose ResourceId disagrees with their own RecurrenceRuleId's.
    [Fact]
    public async Task AKeyAlreadyBoundToADifferentResourceMintsAnIndependentRuleInstead()
    {
        var otherResource = Room();
        var resource = Room();
        var harness = Build(resource);

        var otherRule = new RecurrenceRule(
            Guid.NewGuid(), otherResource.OrgId, otherResource.Id, ActorId, RecurrenceFrequency.Weekly, 1,
            new TimeOnly(9, 0), new TimeOnly(10, 0), StartDate, null, 3, otherResource.TimeZoneId, ActorId, NowUtc);
        var operation = new RecurrenceCreationOperation(
            Guid.NewGuid(), resource.OrgId, ActorId, "key-1", otherRule.Id, NowUtc);
        operation.MarkActive(NowUtc);

        harness.RecurrenceRules.ExistingOperation = operation;
        harness.RecurrenceRules.ExistingRule = otherRule;

        var response = await harness.Handler.Handle(Request(resource, "key-1"), default);

        Assert.NotEqual(otherRule.Id, response.RecurrenceRuleId);
        Assert.NotNull(harness.RecurrenceRules.Added);
        Assert.Equal(resource.Id, harness.RecurrenceRules.Added!.ResourceId);
    }
}
