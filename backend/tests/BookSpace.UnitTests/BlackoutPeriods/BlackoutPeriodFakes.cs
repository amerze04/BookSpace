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

    public int LockCallCount { get; private set; }

    public Task<Resource?> FindOwningResourceAsync(Guid resourceId, CancellationToken cancellationToken) =>
        Task.FromResult(_resource is not null && _resource.Id == resourceId ? _resource : null);

    // P0 hardening: a no-op in this fake — the lock itself is only meaningful
    // against real SQL Server and is exercised by the integration suite. Counted
    // so a test can assert the handler calls it before reading the cancellation
    // candidates.
    public Task LockBookingRangeAsync(
        Guid resourceId, DateTime startsAtUtc, DateTime endsAtUtc, CancellationToken cancellationToken)
    {
        LockCallCount++;
        return Task.CompletedTask;
    }

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

    // Hardening pass, P2. Empty by default — "nothing Pending to withdraw" —
    // which is what every existing blackout test here wants; a test proving
    // the withdraw behavior sets it explicitly.
    public IReadOnlyList<ApprovalRequest> PendingApprovalRequests { get; set; } = [];

    public Task<IReadOnlyList<ApprovalRequest>> FindPendingApprovalRequestsAsync(
        IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApprovalRequest>>(
            PendingApprovalRequests.Where(a => bookingIds.Contains(a.BookingId)).ToList());

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
