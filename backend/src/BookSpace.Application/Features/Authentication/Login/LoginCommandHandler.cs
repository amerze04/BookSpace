using BookSpace.Application.Abstractions;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Authentication.Login;

// FR-2.1 credential login issuing a short-lived access token plus a rotating
// refresh token; FR-2.3 the password is only ever compared against a hash;
// FR-2.4 an inactive user or suspended organization cannot obtain a session.
public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, AuthenticationResult>
{
    private readonly IAuthenticationUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly TokenIssuer _tokenIssuer;
    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        IAuthenticationUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        TokenIssuer tokenIssuer,
        ILogger<LoginCommandHandler> logger)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _logger = logger;
    }

    public async Task<AuthenticationResult> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);
        var found = await _users.FindByEmailAsync(email, cancellationToken);

        // Every failure below throws the same reason code so the endpoint can't be
        // used to tell "no such account" from "wrong password" from "deactivated".
        // The server log carries the real cause; the client does not.
        if (found is null)
        {
            _logger.LogWarning("Login failed: no user for {Email}", email);
            throw InvalidCredentials();
        }

        // Verified before the account-state checks so a wrong password on a
        // deactivated account doesn't take a different amount of work than a
        // wrong password on a live one.
        if (!_passwordHasher.Verify(found.User.PasswordHash, request.Password))
        {
            _logger.LogWarning("Login failed: wrong password for {UserId}", found.User.Id);
            throw InvalidCredentials();
        }

        if (!found.User.IsActive)
        {
            _logger.LogWarning("Login failed: user {UserId} is deactivated", found.User.Id);
            throw InvalidCredentials();
        }

        // Null status = SysAdmin, who has no organization to be suspended.
        if (found.OrganizationStatus is OrganizationStatus.Suspended)
        {
            _logger.LogWarning("Login failed: organization {OrgId} is suspended", found.User.OrgId);
            throw InvalidCredentials();
        }

        // A login starts a brand-new session family; rotation later carries this
        // same FamilyId forward (FR-2.2).
        var (result, _) = _tokenIssuer.Issue(found.User, familyId: Guid.NewGuid());
        await _refreshTokens.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Login succeeded for {UserId}", found.User.Id);
        return result;
    }

    private static AuthenticationException InvalidCredentials() =>
        new(AuthenticationFailureReason.InvalidCredentials, "Invalid email or password.");
}
