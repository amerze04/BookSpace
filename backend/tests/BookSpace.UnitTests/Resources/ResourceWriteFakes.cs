using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests.Resources;

// Hand-written fakes, matching Authentication/AuthenticationFakes.cs — no
// mocking library anywhere in this suite.

internal sealed class FakeResourceRepository : IResourceRepository
{
    private readonly Resource? _resource;

    public FakeResourceRepository(Resource? resource = null, int peakConcurrentBookedQuantity = 0)
    {
        _resource = resource;
        PeakConcurrentBookedQuantity = peakConcurrentBookedQuantity;
    }

    public int PeakConcurrentBookedQuantity { get; }

    public Resource? Added { get; private set; }

    public int SaveChangesCount { get; private set; }

    // True once the capacity check has actually been consulted — the handler is
    // supposed to skip the query entirely unless capacity is going down.
    public bool PeakWasQueried { get; private set; }

    public Task<Resource?> FindForUpdateAsync(Guid resourceId, CancellationToken cancellationToken) =>
        Task.FromResult(_resource is not null && _resource.Id == resourceId ? _resource : null);

    public void Add(Resource resource) => Added = resource;

    public Task<int> PeakConcurrentBookedQuantityAsync(
        Guid resourceId,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        PeakWasQueried = true;
        return Task.FromResult(PeakConcurrentBookedQuantity);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        return Task.CompletedTask;
    }

    // Read side: not exercised by the write handler tests.
    public Task<PagedResult<ListResourcesQueryResponse>> ListAsync(
        ListResourcesQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<GetResourceQueryResponse?> FindDetailAsync(Guid resourceId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

// Accepts a fixed set of ids so a test can state exactly which strings this
// "host" knows, rather than depending on the machine's tzdata. The real
// implementation's IANA-only behavior is covered by its own tests.
internal sealed class FakeTimeZoneCatalog : ITimeZoneCatalog
{
    private readonly HashSet<string> _known;

    public FakeTimeZoneCatalog(params string[] knownIds) =>
        _known = new HashSet<string>(knownIds, StringComparer.Ordinal);

    public bool IsKnownIanaId(string timeZoneId) => _known.Contains(timeZoneId);
}

// No FixedCurrentTenant here on purpose: Persistence/FixedCurrentTenant.cs
// already is one, and these tests use it.
internal sealed class FixedCurrentUser : ICurrentUser
{
    public FixedCurrentUser(Guid? userId) => UserId = userId;

    public Guid? UserId { get; }
}
