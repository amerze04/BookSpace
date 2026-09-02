using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;

// FR-3.4 read side. GET /resources/{id}/blackout-periods, on TenantMember: a
// member choosing when to book needs to see when a resource is blacked out, the
// same reason the read detail carries the availability windows.
//
// **Its own paginated endpoint rather than a collection on GET /resources/{id}**,
// deliberately unlike Phase 3's windows and approvers. Those are bounded sets —
// seven weekdays, a capped approver list — so embedding them costs a known,
// small amount. Blackouts accumulate for the life of the resource and are never
// deleted by the system, so embedding them would make the resource detail grow
// without limit and give a client no way to ask for just the relevant ones.
//
// From/To are an overlap filter, not a containment one: a blackout counts if any
// part of it falls in the window, because a client asking "what blocks next
// week" wants the maintenance that started last Friday and runs through Tuesday.
// Both are optional and default to the whole history, which is the honest
// default for an endpoint whose rows have no natural cut-off.
public sealed record ListBlackoutPeriodsQueryRequest(
    Guid ResourceId,
    DateTime? From = null,
    DateTime? To = null,
    int Page = PagingDefaults.Page,
    int PageSize = PagingDefaults.PageSize,
    string? Sort = null)
    : IRequest<PagedResult<ListBlackoutPeriodsQueryResponse>>, IPagedQuery;
