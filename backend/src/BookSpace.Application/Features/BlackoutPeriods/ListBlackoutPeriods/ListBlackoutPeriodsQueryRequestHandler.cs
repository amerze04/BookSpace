using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;

// FR-3.4. On TenantMember rather than TenantAdmin — see the query for why a
// member needs this.
public sealed class ListBlackoutPeriodsQueryRequestHandler
    : IRequestHandler<ListBlackoutPeriodsQueryRequest, PagedResult<ListBlackoutPeriodsQueryResponse>>
{
    private readonly IBlackoutPeriodRepository _blackouts;

    public ListBlackoutPeriodsQueryRequestHandler(IBlackoutPeriodRepository blackouts)
    {
        _blackouts = blackouts;
    }

    public async Task<PagedResult<ListBlackoutPeriodsQueryResponse>> Handle(
        ListBlackoutPeriodsQueryRequest request,
        CancellationToken cancellationToken)
    {
        // The resource is checked first so an unknown or cross-tenant id is a 404
        // rather than an empty page. An empty page is a real answer here — a
        // resource with no blackouts — so the two have to be distinguishable, or
        // a client cannot tell "nothing blocked" from "no such room". The list
        // query is tenant-filtered too, so this is about the *answer* being
        // honest, not about isolation.
        _ = await _blackouts.FindOwningResourceAsync(request.ResourceId, cancellationToken)
            ?? throw new ResourceNotFoundException(request.ResourceId);

        // The validator has already refused any value not on the whitelist, so
        // this cannot fail here; the return value is ignored rather than
        // re-reported, matching ListResourcesQueryRequestHandler.
        SortOption.TryParse(request.Sort, BlackoutPeriodSortFields.All, out var sort);

        return await _blackouts.ListAsync(request, sort, cancellationToken);
    }
}
