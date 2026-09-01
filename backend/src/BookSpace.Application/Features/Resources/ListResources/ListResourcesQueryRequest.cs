using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ListResources;

// FR-3.1 / FR-3.5. Paged per docs/decisions/0015: defaults live on the
// constructor parameters, so a client that sends no query string gets page 1 of
// 20 with no handler-side normalization.
//
// IncludeArchived defaults to false because FR-3.5 archives rather than deletes:
// without it, every member browsing for something to book would see resources
// that exist only to preserve their booking history. Opting in is how an admin
// reviews them.
public sealed record ListResourcesQueryRequest(
    int Page = PagingDefaults.Page,
    int PageSize = PagingDefaults.PageSize,
    string? Sort = null,
    bool IncludeArchived = false)
    : IRequest<PagedResult<ListResourcesQueryResponse>>, IPagedQuery;
