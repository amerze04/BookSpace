namespace BookSpace.Application.Features.Authentication.Refresh;

// The 200 body of POST /auth/refresh. FR-2.2.
//
// The RefreshToken here is the **rotated** one: the token the caller presented
// is dead by the time this is returned, and re-sending it is treated as theft
// (docs/decisions/0011). A client that ignores this field and keeps the old
// token will kill its own session on the next call.
//
// Its own type rather than login's, per the 2026-09-01 convention — see
// LoginCommandResponse for why the two are expected to diverge.
public sealed record RefreshTokenCommandResponse(string AccessToken, int ExpiresIn, string RefreshToken);
