using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources;
using BookSpace.Application.Features.Resources.GetResource;
using BookSpace.Application.Features.Resources.ListResources;
using BookSpace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// FR-3.1 / FR-3.5 reads. No IgnoreQueryFilters and no TenantBypassScope
// anywhere in this file, deliberately: both queries go through the tenant-
// filtered DbSet, so CLAUDE.md §4.2's query filter and RLS are what make
// another tenant's resource invisible rather than a WHERE clause written here
// that someone could forget.
internal sealed class ResourceRepository : IResourceRepository
{
    private readonly BookSpaceDbContext _context;

    public ResourceRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public Task<PagedResult<ResourceSummaryResponse>> ListAsync(
        ListResourcesQuery query,
        SortOption? sort,
        CancellationToken cancellationToken)
    {
        var resources = _context.Resources.AsQueryable();

        // FR-3.5: archived resources are preserved and stay readable, but they
        // are not what someone browsing for a room wants to see.
        if (!query.IncludeArchived)
        {
            resources = resources.Where(r => !r.IsArchived);
        }

        // The projection happens after ordering so ToPagedResultAsync still
        // sees the OrderBy in the expression tree, and so COUNT(*) runs over
        // the filtered set rather than a materialized list.
        return ApplyOrder(resources, sort)
            .Select(r => new ResourceSummaryResponse(
                r.Id,
                r.Name,
                r.ResourceType,
                r.Capacity,
                r.TimeZoneId,
                r.RequiresApproval,
                r.IsArchived))
            .ToPagedResultAsync(query, cancellationToken);
    }

    // FirstOrDefaultAsync, never DbSet.Find(): Find can return a tracked
    // entity without querying at all, which would skip the query filter
    // (CLAUDE.md §4.2).
    public Task<ResourceDetailResponse?> FindDetailAsync(Guid resourceId, CancellationToken cancellationToken) =>
        _context.Resources
            .Where(r => r.Id == resourceId)
            .Select(r => new ResourceDetailResponse(
                r.Id,
                r.Name,
                r.Description,
                r.ResourceType,
                r.Capacity,
                r.TimeZoneId,
                r.RequiresApproval,
                r.MinDurationMinutes,
                r.MaxDurationMinutes,
                r.IsArchived,
                r.CreatedAtUtc,
                r.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

    // Maps a canonical whitelist name (ResourceSortFields) onto a typed
    // OrderBy. This is the Infrastructure half of the split described on
    // IResourceRepository: the string never enters the expression.
    //
    // Always finishes with ThenBy(Id). Offset paging over a non-unique order
    // has undefined boundaries among tied rows — two resources named "Room A"
    // could appear on both page 1 and page 2 — and ToPagedResultAsync can only
    // detect a *missing* order, not a non-unique one
    // (docs/decisions/0015-api-contract-and-pagination.md).
    private static IOrderedQueryable<Resource> ApplyOrder(IQueryable<Resource> source, SortOption? sort)
    {
        var descending = sort?.Descending ?? false;

        // The default arm covers both "no sort supplied" and sort=name, which
        // is the endpoint's own default order.
        IOrderedQueryable<Resource> ordered = sort?.Field switch
        {
            ResourceSortFields.ResourceType => descending
                ? source.OrderByDescending(r => r.ResourceType)
                : source.OrderBy(r => r.ResourceType),
            ResourceSortFields.Capacity => descending
                ? source.OrderByDescending(r => r.Capacity)
                : source.OrderBy(r => r.Capacity),
            _ => descending
                ? source.OrderByDescending(r => r.Name)
                : source.OrderBy(r => r.Name),
        };

        return ordered.ThenBy(r => r.Id);
    }
}
