using BookSpace.Domain.Entities;

namespace BookSpace.Application.Abstractions;

// FR-2.1 short-lived access token. Claim shape and lifetime are fixed by
// docs/decisions/0009-jwt-claims-and-token-lifetimes.md.
public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

public interface IAccessTokenService
{
    AccessToken Issue(User user);
}
