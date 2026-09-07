using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Domain.Availability;
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

    // Records what the handler stated as inserts. The real repository has to say
    // this explicitly (see IResourceRepository.AddAvailabilityWindows); the fake
    // only has to prove the handler said it.
    public List<AvailabilityWindow> AddedAvailabilityWindows { get; } = new();

    public void AddAvailabilityWindows(IEnumerable<AvailabilityWindow> windows) =>
        AddedAvailabilityWindows.AddRange(windows);

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
    private readonly TimeSpan _offset;

    public FakeTimeZoneCatalog(params string[] knownIds)
        : this(TimeSpan.Zero, knownIds)
    {
    }

    public FakeTimeZoneCatalog(TimeSpan offset, params string[] knownIds)
    {
        _offset = offset;
        _known = new HashSet<string>(knownIds, StringComparer.Ordinal);
    }

    public bool IsKnownIanaId(string timeZoneId) => _known.Contains(timeZoneId);

    // Serves a fixed-offset zone, so a handler test states its own offset instead
    // of depending on the machine's tzdata. The real conversion rules — the two
    // DST cases — are covered by SystemResourceTimeZoneTests, which is the only
    // place a real zone proves anything.
    //
    // Unknown ids throw TimeZoneNotFoundException, matching the real
    // implementation: a stored id that no longer resolves is a 500, not a
    // client error (see ITimeZoneCatalog).
    public IResourceTimeZone GetResourceTimeZone(string timeZoneId) =>
        _known.Contains(timeZoneId)
            ? new FixedOffsetResourceTimeZone(_offset)
            : throw new TimeZoneNotFoundException($"Unknown time zone '{timeZoneId}'.");
}

// A zone with no transitions. Deliberately not in the Availability test folder's
// copy: that one exercises the expansion, this one is arrangement for handler
// tests, and sharing it would couple two unrelated files.
internal sealed class FixedOffsetResourceTimeZone : IResourceTimeZone
{
    private readonly TimeSpan _offset;

    public FixedOffsetResourceTimeZone(TimeSpan offset) => _offset = offset;

    public DateTime ToUtcEarliest(DateTime resourceLocal) => ToUtc(resourceLocal);

    public DateTime ToUtcLatest(DateTime resourceLocal) => ToUtc(resourceLocal);

    public DateTime ToLocal(DateTime utc) =>
        DateTime.SpecifyKind(utc + _offset, DateTimeKind.Unspecified);

    private DateTime ToUtc(DateTime resourceLocal) =>
        DateTime.SpecifyKind(resourceLocal - _offset, DateTimeKind.Utc);
}

// No FixedCurrentTenant here on purpose: Persistence/FixedCurrentTenant.cs
// already is one, and these tests use it.
internal sealed class FixedCurrentUser : ICurrentUser
{
    public FixedCurrentUser(Guid? userId) => UserId = userId;

    public Guid? UserId { get; }
}

// Hand-written, like the rest of this file. Eligibility is stated as a fixed set
// of ids, so a test says exactly who may approve without needing a database,
// roles, or a tenant — the real implementation's three conditions are covered by
// the integration suite, which is where they can actually be exercised.
internal sealed class FakeUserRepository : IUserRepository
{
    private readonly HashSet<Guid> _eligible;
    private readonly Dictionary<Guid, string> _names;

    public FakeUserRepository(params Guid[] eligibleUserIds)
    {
        _eligible = new HashSet<Guid>(eligibleUserIds);
        _names = eligibleUserIds.ToDictionary(id => id, id => $"Approver {id:N}"[..14]);
    }

    // True once eligibility has actually been consulted — the handler is supposed
    // to skip the query entirely for an empty list.
    public bool EligibilityWasQueried { get; private set; }

    public Task<IReadOnlyCollection<Guid>> FindEligibleApproverIdsAsync(
        IReadOnlyCollection<Guid> candidateUserIds,
        CancellationToken cancellationToken)
    {
        EligibilityWasQueried = true;
        return Task.FromResult<IReadOnlyCollection<Guid>>(
            candidateUserIds.Where(_eligible.Contains).ToList());
    }

    public Task<IReadOnlyList<ApproverSummary>> FindApproverSummariesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApproverSummary>>(
            userIds.Where(_names.ContainsKey)
                .Select(id => new ApproverSummary(id, _names[id]))
                .OrderBy(a => a.FullName, StringComparer.Ordinal)
                .ToList());
}
