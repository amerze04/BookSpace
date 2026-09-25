using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Application.Features.Users.GetUserById;
using BookSpace.Application.Features.Users.ListUsers;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

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

    public bool IsInvalidLocalTime(DateTime resourceLocal) => false;

    private DateTime ToUtc(DateTime resourceLocal) =>
        DateTime.SpecifyKind(resourceLocal - _offset, DateTimeKind.Utc);
}

// No FixedCurrentTenant here on purpose: Persistence/FixedCurrentTenant.cs
// already is one, and these tests use it.
internal sealed class FixedCurrentUser : ICurrentUser
{
    private readonly HashSet<Role> _roles;

    public FixedCurrentUser(Guid? userId, params Role[] roles)
    {
        UserId = userId;
        _roles = [.. roles];
    }

    public Guid? UserId { get; }

    // Roles are stated explicitly, and the default is *none* — so a test that
    // does not mention roles gets the least-privileged caller. That matters for
    // the booking reads (WP-4 Phase 2a): a fake that answered true by default
    // would make the admin-widening tests pass without proving anything.
    public bool IsInRole(Role role) => _roles.Contains(role);
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

    // Decision 0028: who an ApprovalRequested notification falls back to when
    // a gated resource has no approvers. Settable so a test can say "this tenant
    // has these admins" without a database; empty by default, which is the
    // least-privileged answer and matches this file's other defaults.
    public List<Guid> TenantAdminUserIds { get; } = new();

    public Task<IReadOnlyCollection<Guid>> FindTenantAdminUserIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(TenantAdminUserIds.ToList());

    public Task<IReadOnlyList<ApproverSummary>> FindApproverSummariesAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApproverSummary>>(
            userIds.Where(_names.ContainsKey)
                .Select(id => new ApproverSummary(id, _names[id]))
                .OrderBy(a => a.FullName, StringComparer.Ordinal)
                .ToList());

    // GET /users' read, deliberately unimplemented here. Nothing in this file's
    // tests lists users, and a hand-written paging/search/sort fake would be a
    // second implementation of decision `0018`'s eligibility rule and of
    // UserScope's two branches — the exact duplication UserRepository states
    // each of them once to avoid. The real one is exercised where it can be:
    // against SQL Server, in UserReadEndpointTests and UserDirectoryEndpointTests.
    public Task<PagedResult<ListUsersQueryResponse>> ListAsync(
        ListUsersQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "FakeUserRepository does not list users; see UserReadEndpointTests.");

    // ---- User management phase 3: POST /users ----
    //
    // Extended here rather than given a second fake, because a second
    // implementation of IUserRepository is a second thing to update every time
    // the interface moves — and the one nobody is looking at is the one that
    // rots. CreateUserCommandRequestHandlerTests uses this.

    public List<User> Added { get; } = [];

    // Counted, not just flagged: the create handler's atomicity claim is that
    // the account, its role and its activation token go in *one* save.
    public int SaveCount { get; private set; }

    // Makes the next save fail exactly as UQ_Users_Email does in the real
    // repository. Modelled as a flag rather than by matching addresses, because
    // the collision this stands in for may be with an account in *another*
    // tenant — which a fake holding only this tenant's users could not see, and
    // which is precisely the case the real path is built not to distinguish.
    public bool NextSaveHitsDuplicateEmail { get; set; }

    public void Add(User user) => Added.Add(user);

    // ---- User management phase 5: the three writes ----

    // The tracked user a write handler mutates. Stated as a list rather than
    // looked up by construction argument, so a test says exactly which people
    // this tenant has.
    public List<User> Users { get; } = [];

    public Task<User?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));

    // ---- Hardening pass, 2026-09-25 (finding 1) ----
    //
    // Projected from the same `Users` list, matching FindDetailAsync below —
    // the fake has one notion of who exists and what state they are in, not two
    // that a test could accidentally let disagree.
    public Task<bool> IsCurrentlyActiveTenantAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = Users.FirstOrDefault(u => u.Id == userId);
        return Task.FromResult(user is not null && user.IsActive && user.Roles.Contains(Role.TenantAdmin));
    }

    // ---- User management phase 7: GET /users/{id} ----
    //
    // Projected from the same `Users` list FindForUpdateAsync reads, so a test
    // that seeds one person's state sees it from both — the fake has no second
    // notion of who exists.
    // Hardening pass, 2026-09-25 (finding 3). A settable set rather than a
    // real ActivationTokens table — this fake has no second aggregate, and a
    // test that cares says so by adding the id here, matching how every other
    // signal on this fake is a plain collection a test populates directly.
    public HashSet<Guid> ActivatedUserIds { get; } = [];

    public Task<GetUserByIdQueryResponse?> FindDetailAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = Users.FirstOrDefault(u => u.Id == userId);
        return Task.FromResult(user is null
            ? null
            : new GetUserByIdQueryResponse(
                user.Id,
                user.FullName,
                user.Email,
                user.IsActive,
                user.Roles.ToList(),
                user.CreatedAtUtc,
                user.UpdatedAtUtc,
                ActivatedUserIds.Contains(user.Id)));
    }

    // The last-admin guard's locking read. Counted from `Users` rather than
    // returned from a fixed field, so a test sets up a tenant and the guard
    // answers the same question the real repository would — the *lock* is the
    // part only the integration suite can prove, and it does
    // (UserWriteEndpointTests' concurrent-removal test).
    public int LastAdminCountQueries { get; private set; }

    public Task<int> CountOtherActiveTenantAdminsAsync(
        Guid excludingUserId,
        CancellationToken cancellationToken)
    {
        LastAdminCountQueries++;

        return Task.FromResult(Users.Count(u =>
            u.Id != excludingUserId && u.IsActive && u.Roles.Contains(Role.TenantAdmin)));
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (NextSaveHitsDuplicateEmail)
        {
            throw new EmailAlreadyInUseException();
        }

        SaveCount++;
        return Task.CompletedTask;
    }
}
