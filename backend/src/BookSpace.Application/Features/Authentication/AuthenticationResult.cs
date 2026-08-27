namespace BookSpace.Application.Features.Authentication;

// FR-2.1. RefreshToken is the raw token — the only moment it exists outside the
// client. ExpiresIn is seconds until the access token expires, the shape OAuth 2
// clients already expect.
public sealed record AuthenticationResult(string AccessToken, int ExpiresIn, string RefreshToken);
