using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.GetUserById;

// GET /users/{id}. User management phase 7, added for the user detail screen
// (docs/user-management-plan.md phase 7): none of the four endpoints
// UsersController already carried returns a single user, and a screen reached
// by a direct link, a bookmark, or a reload needs one that does — the same
// requirement GetResourceQueryRequest answers for a resource.
//
// A deactivated user is still readable by id, matching GetResourceQueryRequest's
// own note about an archived resource: only the directory's default view could
// ever hide one, and it does not (scope=All shows everyone).
public sealed record GetUserByIdQueryRequest(Guid UserId) : IRequest<GetUserByIdQueryResponse>;
