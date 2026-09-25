using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.GetUserById;

// GET /users/{id}. Its own type per decision `0015`'s amendment — response DTOs
// are per-endpoint, never shared — even though every field but the two
// timestamps already exists on ListUsersQueryResponse.
//
// CreatedAtUtc/UpdatedAtUtc are here and deliberately are not on
// ListUsersQueryResponse, mirroring GetResourceQueryResponse: a detail screen
// answers "when did this change", a list row does not need to. The user's own
// CreatedByUserId/UpdatedByUserId stay off the wire for the same reason
// GetResourceQueryResponse leaves them off — a bare Guid a client cannot
// resolve to a person, and one that would leak who administers the tenant.
//
// **IsActivated, added in the 2026-09-25 hardening pass (finding 3).** Whether
// this account has ever completed activation — the only way `User.SetPassword`
// is ever called (see `ActivateAccountCommandRequestHandler`) is by consuming
// an activation token, so this is computed from `ActivationTokens` rather than
// carried as a separate flag on `User` that could drift from it. It is what
// the detail screen uses to decide whether "Resend invitation" makes sense at
// all — resending to an already-activated account is refused server-side
// (`UserAlreadyActivatedException`) and should not be offered in the first
// place.
public sealed record GetUserByIdQueryResponse(
    Guid Id,
    string FullName,
    string Email,
    bool IsActive,
    IReadOnlyList<Role> Roles,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    bool IsActivated);
