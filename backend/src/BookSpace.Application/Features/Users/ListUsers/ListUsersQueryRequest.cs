using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.ListUsers;

// GET /users. TenantAdmin only (see UsersController).
//
// **Two callers, two answers, one route** — and which one you get is `scope`.
//
// It began (admin console phase 1) as the approvers picker's read, and answered
// only the decision `0018` eligible set: own-tenant, active, holding Approver or
// TenantAdmin. That was the whole endpoint, and this header used to explain at
// length why a route named `/users` deliberately answered with a subset.
//
// User management phase 4 added the other caller — the admin console's user
// directory, which needs *every* user in the tenant, Members and deactivated
// accounts included. Rather than a second route, the existing one widened, with
// **the eligible set staying the default** so no existing caller changed by a
// byte. The reasoning is in docs/user-management-plan.md §4.6 and in UserScope
// itself; the short version is that a forgotten parameter has to narrow rather
// than widen, because the failure of widening is an admin being offered somebody
// ReplaceApprovers then refuses with a code that cannot say why.
//
// So: `scope` omitted means the picker's answer. `scope=All` means the
// directory's. Neither reaches past the tenant — that is the query filter's job,
// not this parameter's.
//
// Paged per `0015` like every other list: defaults live on the constructor
// parameters, so a client that sends no query string gets page 1 of 20 with no
// handler-side normalization.
//
// Search matches FullName or Email, mirroring GET /resources' own search
// parameter — a picker, or a directory, over a tenant with a few hundred people
// is a type-to-filter control, not a page-through-it one. Null means "no text
// filter"; whitespace-only is treated as null too, in the repository.
public sealed record ListUsersQueryRequest(
    int Page = PagingDefaults.Page,
    int PageSize = PagingDefaults.PageSize,
    string? Sort = null,
    string? Search = null,
    UserScope Scope = UserScope.EligibleApprovers)
    : IRequest<PagedResult<ListUsersQueryResponse>>, IPagedQuery;
