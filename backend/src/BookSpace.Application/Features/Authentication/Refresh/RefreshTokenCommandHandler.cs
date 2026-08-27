using BookSpace.Application.Abstractions;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Authentication.Refresh;

// FR-2.2 in full: refresh tokens rotate on use, and a reused (i.e. already
// rotated, therefore possibly stolen) token invalidates the whole session
// family. FR-2.4: a user or organization that has since been deactivated loses
// the session on its next refresh.
//
// The five cases, and why they differ:
//   unknown hash        -> 401. Nothing to revoke; there is no family to trust.
//   active + unexpired  -> rotate. Old revoked and pointed at its replacement.
//   expired, not revoked-> 401, revoke that token only. Expiry is normal
//                          lifecycle, not evidence of theft.
//   already revoked     -> REUSE. The legitimate holder rotated this token
//                          already, so whoever just presented it has a copy
//                          they shouldn't. Kill the family.
//   inactive user/org   -> 401 and kill the family (FR-2.4).
public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, AuthenticationResult>
{
    private readonly IAuthenticationUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IRefreshTokenFactory _refreshTokenFactory;
    private readonly TokenIssuer _tokenIssuer;
    private readonly IClock _clock;
    private readonly ILogger<RefreshTokenCommandHandler> _logger;

    public RefreshTokenCommandHandler(
        IAuthenticationUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IRefreshTokenFactory refreshTokenFactory,
        TokenIssuer tokenIssuer,
        IClock clock,
        ILogger<RefreshTokenCommandHandler> logger)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _refreshTokenFactory = refreshTokenFactory;
        _tokenIssuer = tokenIssuer;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AuthenticationResult> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var presentedHash = _refreshTokenFactory.HashOf(request.RefreshToken);
        var stored = await _refreshTokens.FindByHashAsync(presentedHash, cancellationToken);

        if (stored is null)
        {
            _logger.LogWarning("Refresh failed: no token matches the presented hash");
            throw new AuthenticationException(
                AuthenticationFailureReason.InvalidRefreshToken, "The refresh token is not valid.");
        }

        // Reuse detection comes first: an already-revoked token is the signal
        // that matters, and it outranks every other check.
        if (!stored.IsActive)
        {
            _logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; revoking family {FamilyId}",
                stored.UserId,
                stored.FamilyId);

            await _refreshTokens.RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await _refreshTokens.SaveChangesAsync(cancellationToken);

            throw new AuthenticationException(
                AuthenticationFailureReason.RefreshTokenReuseDetected,
                "The refresh token has already been used. The session family has been revoked.");
        }

        if (stored.ExpiresAtUtc <= now)
        {
            // Expiry is not theft — revoke this token alone and leave the family be.
            stored.Revoke(now);
            await _refreshTokens.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("Refresh failed: token expired for user {UserId}", stored.UserId);
            throw new AuthenticationException(
                AuthenticationFailureReason.RefreshTokenExpired, "The refresh token has expired.");
        }

        var found = await _users.FindByIdAsync(stored.UserId, cancellationToken);

        // FR-2.4: deactivated user or suspended org loses access at the next
        // refresh. The family goes with it — there is no route back to a session.
        if (found is null
            || !found.User.IsActive
            || found.OrganizationStatus is OrganizationStatus.Suspended)
        {
            _logger.LogWarning(
                "Refresh failed: user {UserId} is no longer eligible; revoking family {FamilyId}",
                stored.UserId,
                stored.FamilyId);

            await _refreshTokens.RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await _refreshTokens.SaveChangesAsync(cancellationToken);

            throw new AuthenticationException(
                AuthenticationFailureReason.AccountInactive, "The account is no longer active.");
        }

        // Rotation: same family, and the replacement inherits this token's expiry
        // rather than starting a fresh window, keeping the family lifetime absolute.
        var (result, issued) = _tokenIssuer.Issue(
            found.User,
            familyId: stored.FamilyId,
            expiresAtUtc: stored.ExpiresAtUtc);

        stored.Revoke(now, issued.Id);

        // One SaveChangesAsync for the revoke and the insert together, so the
        // implicit transaction covers both and EnableRetryOnFailure can retry the
        // pair. RevokedAtUtc is a concurrency token, so a second request racing
        // to rotate this same token affects zero rows and surfaces as a
        // DbUpdateConcurrencyException (409) rather than minting a second
        // replacement — see RefreshTokenConfiguration.
        await _refreshTokens.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Refresh token rotated for user {UserId} in family {FamilyId}",
            found.User.Id,
            stored.FamilyId);

        return result;
    }
}
