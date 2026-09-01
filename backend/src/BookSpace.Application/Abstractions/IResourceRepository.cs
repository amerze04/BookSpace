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
