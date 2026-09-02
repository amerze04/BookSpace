using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// The read port for the Resource aggregate. Same shape as
// IAuthenticationUserRepository: declared here, implemented in
// BookSpace.Infrastructure, because CLAUDE.md §3 keeps EF Core out of
// BookSpace.Application entirely.
//
// The reads return DTOs rather than entities, which makes the handlers thin —
// deliberately. Projecting in SQL needs an IQueryable, and IQueryable is EF, so
// the projection has to happen on the Infrastructure side of this line. The
// alternative (load whole entities, map in the handler) would select every
// column of every row to build a summary of seven.
//
// Sorting is split across the same line for the same reason: the Application
// layer owns the contract (ResourceSortFields is the whitelist, and the
// validator refuses anything else), and the implementation turns the canonical
// field name into a typed OrderBy. A sort string never reaches a LINQ
// expression as a string.
//
// Nothing here needs IgnoreQueryFilters: unlike authentication, these run
// inside a tenant context, so the global query filter and RLS are exactly what
// makes another tenant's id return null (AC-4).
public interface IResourceRepository
{
    Task<PagedResult<ListResourcesQueryResponse>> ListAsync(
        ListResourcesQueryRequest query,
        SortOption? sort,
        CancellationToken cancellationToken);

    // Null means "no such resource in this tenant" — which, per ErrorKind
    // .NotFound, is also the answer for another tenant's real id.
    Task<GetResourceQueryResponse?> FindDetailAsync(Guid resourceId, CancellationToken cancellationToken);

    // ---- Writes (FR-3.1 / FR-3.5) ----

    // The tracked aggregate, for an edit. Returns the entity rather than a DTO
    // because the caller mutates it through the domain methods — the same
    // reason IAuthenticationUserRepository returns a User. Loads the approver
    // assignments and availability windows with it, since the edit rules read
    // both (ApproversRequired, and the timezone-change notice's count).
    Task<Resource?> FindForUpdateAsync(Guid resourceId, CancellationToken cancellationToken);

    void Add(Resource resource);

    // The windows a ReplaceAvailabilityWindows produced, stated as new rows.
    //
    // Necessary, and the reason is a genuine EF Core trap: an entity discovered
    // through a collection navigation is marked **Modified** rather than Added
    // when its key is already set — the same "is the key set?" heuristic
    // DbContext.Update uses on a graph. A freshly minted AvailabilityWindow
    // carries a Guid the caller chose (Resource mints no ids, CLAUDE.md's
    // house style), so EF issues an UPDATE against a row that does not exist,
    // affects zero rows, and throws DbUpdateConcurrencyException — a 409 for
    // what is really an insert.
    //
    // Resource.AddAvailabilityWindow has the same exposure and does not show it
    // today only because its one caller (SeedData) adds windows to a resource
    // that is itself Added, so the children cascade to Added with it.
    //
    // Removals need no equivalent: clearing the collection leaves EF with
    // orphans it correctly marks Deleted, since it can see them leave.
    void AddAvailabilityWindows(IEnumerable<AvailabilityWindow> windows);

    // The largest number of units any single instant still in the future has
    // already committed on this resource — the number a capacity decrease must
    // not fall below (ReasonCodes.CapacityBelowExistingBookings).
    //
    // "Concurrent units", per docs/decisions/0005-capacity-semantics.md: not a
    // booking count and not a sum over the whole range, but the peak of
    // overlapping Quantity. Past bookings are excluded on purpose — reducing
    // capacity cannot invalidate history, and FR-3.5's whole premise is that
    // history is preserved as it was.
    Task<int> PeakConcurrentBookedQuantityAsync(
        Guid resourceId,
        DateTime asOfUtc,
        CancellationToken cancellationToken);

    // Separate from the mutations, matching IRefreshTokenRepository: the handler
    // owns the unit of work, so one save covers everything it changed.
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
