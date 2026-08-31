using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;

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
    Task<PagedResult<ResourceSummaryResponse>> ListAsync(
        ListResourcesQuery query,
        SortOption? sort,
        CancellationToken cancellationToken);

    // Null means "no such resource in this tenant" — which, per ErrorKind
    // .NotFound, is also the answer for another tenant's real id.
    Task<ResourceDetailResponse?> FindDetailAsync(Guid resourceId, CancellationToken cancellationToken);
}
