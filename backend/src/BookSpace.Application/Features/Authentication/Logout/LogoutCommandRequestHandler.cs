using BookSpace.Application.Abstractions;
using BookSpace.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Authentication.Logout;

// FR-2.4. Revokes the whole family, not just the presented token: logging out
// should end the session, and a family is exactly one session's chain of
// rotations. Succeeds either way — an unknown token is treated as "already
// logged out" rather than reported, so this endpoint reveals nothing.
public sealed class LogoutCommandRequestHandler : IRequestHandler<LogoutCommandRequest, Unit>
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IRefreshTokenFactory _refreshTokenFactory;
    private readonly IClock _clock;
    private readonly ILogger<LogoutCommandRequestHandler> _logger;

    public LogoutCommandRequestHandler(
        IRefreshTokenRepository refreshTokens,
        IRefreshTokenFactory refreshTokenFactory,
        IClock clock,
        ILogger<LogoutCommandRequestHandler> logger)
    {
        _refreshTokens = refreshTokens;
        _refreshTokenFactory = refreshTokenFactory;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Unit> Handle(LogoutCommandRequest request, CancellationToken cancellationToken)
    {
        var presentedHash = _refreshTokenFactory.HashOf(request.RefreshToken);
        var stored = await _refreshTokens.FindByHashAsync(presentedHash, cancellationToken);

        if (stored is null)
        {
            _logger.LogInformation("Logout requested with an unrecognized refresh token; nothing to revoke");
            return Unit.Value;
        }

        await _refreshTokens.RevokeFamilyAsync(stored.FamilyId, _clock.UtcNow, cancellationToken);
        await _refreshTokens.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Logout revoked family {FamilyId} for user {UserId}", stored.FamilyId, stored.UserId);

        return Unit.Value;
    }
}
