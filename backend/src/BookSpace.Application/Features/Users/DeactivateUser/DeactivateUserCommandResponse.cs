using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.DeactivateUser;

// The 200 body of POST /users/{id}/deactivate.
//
// Its own type, not shared with the sibling writes, per the convention agreed
// 2026-09-01 — four near-identical records beat one shape that four endpoints
// cannot change independently. This is the one most likely to diverge: a
// deactivation response is the plausible place for "and here is what this person
// still has booked", which belongs in neither of the others.
public sealed record DeactivateUserCommandResponse(
    Guid Id,
    string Email,
    string FullName,
    bool IsActive,
    IReadOnlyList<Role> Roles,
    DateTime UpdatedAtUtc);
