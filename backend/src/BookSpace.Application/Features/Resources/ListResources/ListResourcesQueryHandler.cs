using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ListResources;

// FR-3.1. Reads are on the TenantMember policy, not TenantAdmin: a member has
// to be able to browse resources in order to book one (see the controller).
public sealed class ListResourcesQueryHandler
    : IRequestHandler<ListResourcesQuery, PagedResult<ResourceSummaryResponse>>
{
    private readonly IResourceRepository _resources;

    public ListResourcesQueryHandler(IResourceRepository resources)
    {
        _resources = resources;
    }

    public Task<PagedResult<ResourceSummaryResponse>> Handle(
        ListResourcesQuery request,
        CancellationToken cancellationToken)
    {
        // The validator has already refused any value not on the whitelist, so
        // this cannot fail here; the return value is ignored rather than
        // re-reported. Parsing here instead of threading a parsed SortOption
        // through the query keeps the query a plain wire shape.
        SortOption.TryParse(request.Sort, ResourceSortFields.All, out var sort);

        return _resources.ListAsync(request, sort, cancellationToken);
    }
}
