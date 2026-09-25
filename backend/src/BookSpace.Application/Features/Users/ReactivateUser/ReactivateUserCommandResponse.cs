using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.ReactivateUser;

// The 200 body of POST /users/{id}/reactivate. Its own type — see
// DeactivateUserCommandResponse for the convention and why the duplication is
// deliberate rather than an oversight.
public sealed record ReactivateUserCommandResponse(
    Guid Id,
    string Email,
    string FullName,
    bool IsActive,
    IReadOnlyList<Role> Roles,
    DateTime UpdatedAtUtc);
