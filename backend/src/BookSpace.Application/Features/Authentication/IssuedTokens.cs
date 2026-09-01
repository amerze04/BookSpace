namespace BookSpace.Application.Features.Authentication;

// What TokenIssuer hands back — an internal collaborator's return value, not an
// endpoint's response.
//
// This used to be AuthenticationResult, a single response DTO that both the
// login and refresh endpoints returned. Under the 2026-09-01 convention each
// endpoint owns its own response type, so the two split into
// LoginCommandResponse and RefreshTokenCommandResponse and this stayed behind as
// what it always really was: the issuer's output, which the handlers map onto
// their own contracts. That separation is worth having on its own merits — the
// issuer is shared by design (FR-2.1 and FR-2.2 must mint tokens identically),
// while the endpoints are free to diverge.
public sealed record IssuedTokens(string AccessToken, int ExpiresIn, string RefreshToken);
