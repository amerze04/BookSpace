using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Resources.GetResourceAvailability;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests.Resources;

// The handler's own job, which is small on purpose: 404 on an unknown resource,
// the archived short-circuit, and mapping. The calculation it delegates to is
// covered by AvailabilityCalculatorTests, and the queries it delegates to are
// covered by the integration suite.
public class GetResourceAvailabilityQueryRequestHandlerTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTime NowUtc = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

    // 2026-09-07 is a Monday. The fake zone is UTC, so local times are instants.
    private static readonly DateOnly Monday = new(2026, 9, 7);

    private const string TimeZoneId = "America/New_York";

    private static Resource Room(bool archived = false, int capacity = 4)
    {
        var resource = new Resource(
            Guid.NewGuid(), OrgId, "Conference Room A", "Room", capacity,
            timeZoneId: TimeZoneId, requiresApproval: false,
            minDurationMinutes: null, maxDurationMinutes: null,
            description: null, createdByUserId: ActorId, nowUtc: NowUtc);

        resource.ReplaceAvailabilityWindows(
            [new AvailabilityWindowDefinition(
                Guid.NewGuid(), DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(17, 0))],
            ActorId,
            NowUtc);

        if (archived)
        {
            resource.Archive(ActorId, NowUtc);
        }

        return resource;
    }

    private static GetResourceAvailabilityQueryRequestHandler Handler(FakeAvailabilityRepository repository) =>
        new(repository, new FakeTimeZoneCatalog(TimeZoneId));

    [Fact]
    public async Task ReturnsTheBookableIntervalsAndEchoesTheRange()
    {
        var resource = Room();
        var repository = new FakeAvailabilityRepository(resource);

        var response = await Handler(repository).Handle(
            new GetResourceAvailabilityQueryRequest(resource.Id, Monday, Monday),
            CancellationToken.None);

        Assert.Equal(resource.Id, response.ResourceId);
        Assert.Equal(TimeZoneId, response.TimeZoneId);
        Assert.Equal(Monday, response.FromLocalDate);
        Assert.Equal(Monday, response.ToLocalDate);
        Assert.False(response.IsArchived);

        var interval = Assert.Single(response.Intervals);
        Assert.Equal(new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Utc), interval.StartUtc);
        Assert.Equal(new DateTime(2026, 9, 7, 17, 0, 0, DateTimeKind.Utc), interval.EndUtc);
        Assert.Equal(4, interval.RemainingCapacity);
    }

    // Also the cross-tenant path: §4.2's filters make another tenant's real id
    // return null from the repository, and ResourceNotFound is the only thing the
    // handler may say about it (AC-4).
    [Fact]
    public async Task Throws_WhenTheResourceIsNotInTheCallersTenant()
    {
        var repository = new FakeAvailabilityRepository(resource: null);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Handler(repository).Handle(
            new GetResourceAvailabilityQueryRequest(Guid.NewGuid(), Monday, Monday),
            CancellationToken.None));
    }

    // Owner's call, 2026-09-03: FR-3.5 keeps an archived resource readable, and
    // "nothing is bookable" is the true answer — not 422 ResourceArchived, which
    // is a write-side refusal.
    [Fact]
    public async Task AnArchivedResourceReturnsAnEmptyListRatherThanAnError()
    {
        var resource = Room(archived: true);
        var repository = new FakeAvailabilityRepository(resource);

        var response = await Handler(repository).Handle(
            new GetResourceAvailabilityQueryRequest(resource.Id, Monday, Monday),
            CancellationToken.None);

        Assert.True(response.IsArchived);
        Assert.Empty(response.Intervals);
    }

    // The flag is what makes an empty list readable: without it, archived and
    // "open at no time this week" are the same response.
    [Fact]
    public async Task AnEmptyListFromAClosedScheduleIsNotFlaggedArchived()
    {
        var resource = Room();
        var repository = new FakeAvailabilityRepository(resource);

        // Tuesday: the schedule only has a Monday window.
        var response = await Handler(repository).Handle(
            new GetResourceAvailabilityQueryRequest(resource.Id, Monday.AddDays(1), Monday.AddDays(1)),
            CancellationToken.None);

        Assert.False(response.IsArchived);
        Assert.Empty(response.Intervals);
    }

    // Short-circuited before the blackout and booking queries, since neither
    // could change the answer. Asserted because it is the kind of optimisation
    // that quietly stops holding.
    [Fact]
    public async Task AnArchivedResourceCostsOneQuery()
    {
        var repository = new FakeAvailabilityRepository(Room(archived: true));

        await Handler(repository).Handle(
            new GetResourceAvailabilityQueryRequest(repository.Resource!.Id, Monday, Monday),
            CancellationToken.None);

        Assert.Equal(0, repository.BlackoutQueries);
        Assert.Equal(0, repository.BookingQueries);
    }

    // The span the blackout and booking queries are scoped to has to be the same
    // one the expansion works over, or a booking at the edge of the range would be
    // fetched and then ignored — or worse, not fetched at all.
    [Fact]
    public async Task ScopesTheRowFetchToTheLocalRangesOwnUtcBounds()
    {
        var resource = Room();
        var repository = new FakeAvailabilityRepository(resource);

        await Handler(repository).Handle(
            new GetResourceAvailabilityQueryRequest(resource.Id, Monday, Monday.AddDays(1)),
            CancellationToken.None);

        Assert.Equal(
            new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
            repository.RequestedSpan!.Value.StartUtc);
        Assert.Equal(
            new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc),
            repository.RequestedSpan!.Value.EndUtc);
    }

    [Fact]
    public async Task SubtractsTheBlackoutsAndBookingsTheRepositoryReturns()
    {
        var resource = Room();
        var repository = new FakeAvailabilityRepository(
            resource,
            blackouts: [Interval(12, 13)],
            bookings: [new BookedQuantity(Interval(14, 15), 1)]);

        var response = await Handler(repository).Handle(
            new GetResourceAvailabilityQueryRequest(resource.Id, Monday, Monday),
            CancellationToken.None);

        Assert.Equal(
            new[]
            {
                (Interval(9, 12).StartUtc, Interval(9, 12).EndUtc, 4),
                (Interval(13, 14).StartUtc, Interval(13, 14).EndUtc, 4),
                (Interval(14, 15).StartUtc, Interval(14, 15).EndUtc, 3),
                (Interval(15, 17).StartUtc, Interval(15, 17).EndUtc, 4),
            },
            response.Intervals.Select(i => (i.StartUtc, i.EndUtc, i.RemainingCapacity)));
    }

    private static UtcInterval Interval(int startHour, int endHour) =>
        new(
            new DateTime(2026, 9, 7, startHour, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 7, endHour, 0, 0, DateTimeKind.Utc));

    // Hand-written, like the rest of this suite's fakes. Records what it was
    // asked for, because two of the handler's responsibilities — the archived
    // short-circuit and the span it scopes the fetch to — are only observable
    // that way.
    private sealed class FakeAvailabilityRepository : IAvailabilityRepository
    {
        private readonly IReadOnlyList<UtcInterval> _blackouts;
        private readonly IReadOnlyList<BookedQuantity> _bookings;

        public FakeAvailabilityRepository(
            Resource? resource,
            IReadOnlyList<UtcInterval>? blackouts = null,
            IReadOnlyList<BookedQuantity>? bookings = null)
        {
            Resource = resource;
            _blackouts = blackouts ?? [];
            _bookings = bookings ?? [];
        }

        public Resource? Resource { get; }

        public UtcInterval? RequestedSpan { get; private set; }

        public int BlackoutQueries { get; private set; }

        public int BookingQueries { get; private set; }

        public Task<Resource?> FindWithScheduleAsync(Guid resourceId, CancellationToken cancellationToken) =>
            Task.FromResult(Resource is not null && Resource.Id == resourceId ? Resource : null);

        public Task<IReadOnlyList<UtcInterval>> FindBlackoutIntervalsAsync(
            Guid resourceId,
            UtcInterval span,
            CancellationToken cancellationToken)
        {
            RequestedSpan = span;
            BlackoutQueries++;
            return Task.FromResult(_blackouts);
        }

        public Task<IReadOnlyList<BookedQuantity>> FindBookedQuantitiesAsync(
            Guid resourceId,
            UtcInterval span,
            CancellationToken cancellationToken)
        {
            RequestedSpan = span;
            BookingQueries++;
            return Task.FromResult(_bookings);
        }
    }
}
