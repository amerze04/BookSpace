using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;

namespace BookSpace.Application.Features.Authentication;

// Public because the handlers that consume it are (their constructors are part
// of the DI contract); it is still application-internal by intent — nothing
// outside this layer has a reason to mint a token pair.
// The one place an access token + refresh token pair is minted, shared by login
// and rotation so the two can't drift apart. Adds the new refresh token to the
// repository but does not save — the caller decides what else belongs in the
// same unit of work.
public sealed class TokenIssuer
{
    private readonly IAccessTokenService _accessTokenService;
    private readonly IRefreshTokenFactory _refreshTokenFactory;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IClock _clock;

    public TokenIssuer(
        IAccessTokenService accessTokenService,
        IRefreshTokenFactory refreshTokenFactory,
        IRefreshTokenRepository refreshTokens,
        IClock clock)
    {
        _accessTokenService = accessTokenService;
        _refreshTokenFactory = refreshTokenFactory;
        _refreshTokens = refreshTokens;
        _clock = clock;
    }

    // familyId: a fresh Guid at login, the existing family on rotation (FR-2.2).
    // expiresAtUtc: null at login (start the absolute window now), or the
    // rotated token's own expiry, which rotation inherits rather than extending.
    public (AuthenticationResult Result, RefreshToken Issued) Issue(
        User user,
        Guid familyId,
        DateTime? expiresAtUtc = null)
    {
        var now = _clock.UtcNow;
        var accessToken = _accessTokenService.Issue(user);
        var generated = _refreshTokenFactory.Create();

        var refreshToken = new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            generated.Hash,
            familyId,
            issuedAtUtc: now,
            expiresAtUtc: expiresAtUtc ?? now.Add(_refreshTokenFactory.Lifetime));

        _refreshTokens.Add(refreshToken);

        var expiresIn = (int)Math.Max(0, Math.Round((accessToken.ExpiresAtUtc - now).TotalSeconds));
        return (new AuthenticationResult(accessToken.Token, expiresIn, generated.RawToken), refreshToken);
    }
}
