using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.ListUsers;

// FR-3.3's missing half. TenantAdmin-only at the controller; nothing here
// re-checks the role, the same way no other handler does — authorization is one
// mechanism (decision `0012`), not a policy plus a hand-written if.
public sealed class ListUsersQueryRequestHandler
    : IRequestHandler<ListUsersQueryRequest, PagedResult<ListUsersQueryResponse>>
{
    private readonly IUserRepository _users;

    public ListUsersQueryRequestHandler(IUserRepository users)
    {
        _users = users;
    }

    public Task<PagedResult<ListUsersQueryResponse>> Handle(
        ListUsersQueryRequest request,
        CancellationToken cancellationToken)
    {
        // The validator has already refused any value not on the whitelist, so
        // this cannot fail here; the return value is ignored rather than
        // re-reported. Parsing here instead of threading a parsed SortOption
        // through the query keeps the query a plain wire shape — same shape as
        // ListResourcesQueryRequestHandler.
        SortOption.TryParse(request.Sort, UserSortFields.All, out var sort);

        return _users.ListEligibleApproversAsync(request, sort, cancellationToken);
    }
}
