using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests.BlackoutPeriods;

// Hand-written fakes, matching Resources/ResourceWriteFakes.cs — no mocking
// library anywhere in this suite.
internal sealed class FakeBlackoutPeriodRepository : IBlackoutPeriodRepository
{
    private readonly Resource? _resource;
    private readonly List<Booking> _bookingsToCancel;
    private readonly BlackoutPeriod? _existing;

    public FakeBlackoutPeriodRepository(
        Resource? resource = null,
        IEnumerable<Booking>? bookingsToCancel = null,
        BlackoutPeriod? existing = null)
    {
        _resource = resource;
        _bookingsToCancel = bookingsToCancel?.ToList() ?? new List<Booking>();
        _existing = existing;
    }

    public BlackoutPeriod? Added { get; private set; }

    public BlackoutPeriod? Removed { get; private set; }

    public List<Notification> AddedNotifications { get; } = new();

    public int SaveChangesCount { get; private set; }

    // What the handler asked for, so a test can assert the cascade window is the
    // blackout's own interval and that the clock was passed through.
    public (Guid ResourceId, DateTime StartsAtUtc, DateTime EndsAtUtc, DateTime NowUtc)? CascadeQuery
    {
        get;
        private set;
    }

    public Task<Resource?> FindOwningResourceAsync(Guid resourceId, CancellationToken cancellationToken) =>
        Task.FromResult(_resource is not null && _resource.Id == resourceId ? _resource : null);

    public Task<IReadOnlyList<Booking>> FindBookingsToCancelAsync(
        Guid resourceId,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        CascadeQuery = (resourceId, startsAtUtc, endsAtUtc, nowUtc);

        // Returns whatever the test staged, unfiltered. The real repository's
        // predicate is exercised by the integration suite against SQL Server,
        // which is the only place it can be — and the domain guard in
        // CancelForBlackout is what makes an over-broad filter fail loudly
        // rather than silently cancel the wrong row.
        return Task.FromResult<IReadOnlyList<Booking>>(_bookingsToCancel);
    }

    // Matched on both ids, like the real repository: a test can therefore prove
    // that a blackout belonging to a different resource is not found.
    public Task<BlackoutPeriod?> FindForUpdateAsync(
        Guid resourceId,
        Guid blackoutPeriodId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            _existing is not null && _existing.Id == blackoutPeriodId && _existing.ResourceId == resourceId
                ? _existing
                : null);

    public void Add(BlackoutPeriod blackoutPeriod) => Added = blackoutPeriod;

    public void Remove(BlackoutPeriod blackoutPeriod) => Removed = blackoutPeriod;

    public void AddNotifications(IEnumerable<Notification> notifications) =>
        AddedNotifications.AddRange(notifications);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        return Task.CompletedTask;
    }

    // Read side: not exercised by the create handler tests.
    public Task<PagedResult<ListBlackoutPeriodsQueryResponse>> ListAsync(
        ListBlackoutPeriodsQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
