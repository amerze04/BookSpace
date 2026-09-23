using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.ListUsers;

// Admin console phase 1. The one backend addition the console needs, and it
// exists for exactly one reason: PUT /resources/{id}/approvers takes
// approverUserIds, and until now *nothing in the API listed users at all*. An
// admin could see who was already assigned to a resource (GET /resources/{id}
// returns their names) and had no way whatever to discover who else they could
// assign. See docs/admin-plan.md §2.
//
// **The result set is deliberately narrower than the route suggests**: it is the
// decision `0018` eligible set — own-tenant, active, holding Approver or
// TenantAdmin — not every user in the tenant. Three reasons, in order of weight:
//
//   1. It is the set the only caller needs. An ineligible id sent to
//      ReplaceApprovers comes back ApproverNotEligible, and `0018` collapses all
//      three ineligibility reasons into that one code on purpose — so the UI
//      cannot explain *why* someone was refused. Never offering them is the only
//      honest way to build that picker.
//   2. It reuses an eligibility rule the codebase already enforces rather than
//      writing a second definition of "eligible" that could drift from
//      IUserRepository.FindEligibleApproverIdsAsync.
//   3. It is the narrowest endpoint that makes the screen possible. A general
//      user directory is a bigger contract than anything has asked for, and user
//      management is explicitly out of scope (docs/admin-plan.md §3).
//
// Paged per `0015` like every other list: defaults live on the constructor
// parameters, so a client that sends no query string gets page 1 of 20 with no
// handler-side normalization.
//
// Search matches FullName or Email, mirroring GET /resources' own search
// parameter — a picker over a tenant with a few hundred eligible users is a
// type-to-filter control, not a page-through-it one. Null means "no text
// filter"; whitespace-only is treated as null too, in the repository.
public sealed record ListUsersQueryRequest(
    int Page = PagingDefaults.Page,
    int PageSize = PagingDefaults.PageSize,
    string? Sort = null,
    string? Search = null)
    : IRequest<PagedResult<ListUsersQueryResponse>>, IPagedQuery;
