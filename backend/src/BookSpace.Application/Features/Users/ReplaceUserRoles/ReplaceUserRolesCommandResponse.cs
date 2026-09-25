using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.ReplaceUserRoles;

// The 200 body of PUT /users/{id}/roles — the stored set, which for a
// replace-the-set write is the only honest confirmation that it took.
//
// Its own type; see DeactivateUserCommandResponse for the convention.
public sealed record ReplaceUserRolesCommandResponse(
    Guid Id,
    string Email,
    string FullName,
    bool IsActive,
    IReadOnlyList<Role> Roles,
    DateTime UpdatedAtUtc);
