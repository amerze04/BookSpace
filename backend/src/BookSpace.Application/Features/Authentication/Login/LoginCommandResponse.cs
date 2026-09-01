namespace BookSpace.Application.Features.Authentication.Login;

// The 200 body of POST /auth/login. FR-2.1.
//
// ExpiresIn is seconds until the access token expires — the shape OAuth 2
// clients already expect. RefreshToken is the raw token, and this is the only
// moment it exists outside the client (docs/decisions/0011 stores only its
// hash).
//
// Identical to RefreshTokenCommandResponse today, and a separate type anyway
// (convention agreed 2026-09-01): the two endpoints are free to diverge, and
// login is the one that would — it is the plausible place for a "must change
// password" flag or the user's display name, neither of which belongs in a
// refresh response.
public sealed record LoginCommandResponse(string AccessToken, int ExpiresIn, string RefreshToken);
