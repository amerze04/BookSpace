using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.GetUserById;

// The 404 path is also the cross-tenant path: the global query filter and RLS
// (CLAUDE.md §4.2) make another tenant's real id return null here, and
// UserNotFound is the only thing this may say about it — confirming that an id
// exists somewhere is exactly the leak AC-4 forbids. The same answer
// DeactivateUser/ReactivateUser/ReplaceUserRoles already give an unknown id.
public sealed class GetUserByIdQueryRequestHandler
    : IRequestHandler<GetUserByIdQueryRequest, GetUserByIdQueryResponse>
{
    private readonly IUserRepository _users;

    public GetUserByIdQueryRequestHandler(IUserRepository users)
    {
        _users = users;
    }

    public async Task<GetUserByIdQueryResponse> Handle(
        GetUserByIdQueryRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _users.FindDetailAsync(request.UserId, cancellationToken);

        // The message is for the log only (see AppException) — the response
        // carries the reason code and nothing else.
        return user ?? throw new UserNotFoundException(request.UserId);
    }
}
