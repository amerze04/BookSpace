using BookSpace.Application.Abstractions;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Authentication.Login;

// FR-2.1 credential login issuing a short-lived access token plus a rotating
// refresh token; FR-2.3 the password is only ever compared against a hash;
// FR-2.4 an inactive user or suspended organization cannot obtain a session.
public sealed class LoginCommandRequestHandler : IRequestHandler<LoginCommandRequest, LoginCommandResponse>
{
    // Hardening pass, P2 security. A fixed, valid-format PBKDF2 hash with no
    // real password behind it, so IPasswordHasher.Verify below still runs its
    // full 100k-iteration cost. Its only job is to make the nonexistent-user
    // path pay the same work the real-user path always paid, so response
    // timing stops being a statistically observable account-existence oracle.
    // The result of verifying against it is never read for anything but
    // discarding: it must be architecturally impossible for a coincidental
    // match here to be mistaken for a successful login.
    //
    // A literal, pre-computed hash — not IPasswordHasher.Hash(...) called
    // here — deliberately: this is BookSpace.Application, which CLAUDE.md §3
    // keeps free of BookSpace.Infrastructure's concrete PasswordHasherAdapter,
    // and computing it fresh via the injected interface on every handler
    // construction would repeat the very cost this constant exists to pay
    // exactly once, ahead of time. Produced by
    // PasswordHasherAdapter().Hash("no real account uses this password") —
    // regenerating it is safe any time; nothing decodes or depends on its
    // specific bytes beyond "a hash Verify will spend real PBKDF2 work on".
    private const string DummyPasswordHash =
        "AQAAAAIAAYagAAAAEDCP5FwLtICvnB+dL5Mk9d5LxaSIn0Nsft9mp+IPyHkZJrsH0iwv3f3Me0u9EEgP1g==";

    private readonly IAuthenticationUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly TokenIssuer _tokenIssuer;
    private readonly ILogger<LoginCommandRequestHandler> _logger;

    public LoginCommandRequestHandler(
        IAuthenticationUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        TokenIssuer tokenIssuer,
        ILogger<LoginCommandRequestHandler> logger)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _logger = logger;
    }

    public async Task<LoginCommandResponse> Handle(LoginCommandRequest request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);
        var found = await _users.FindByEmailAsync(email, cancellationToken);

        // Every failure below throws the same reason code so the endpoint can't be
        // used to tell "no such account" from "wrong password" from "deactivated".
        // The server log carries the real cause; the client does not.
        if (found is null)
        {
            // Hardening pass, P2 security: verified against a fixed dummy
            // hash before failing, so this path costs the same PBKDF2 work
            // the real-user branch below always paid — otherwise a
            // nonexistent email returns in microseconds while a wrong
            // password on a real one costs a full KDF round, and that gap is
            // a remotely observable account-enumeration oracle no reason
            // code hides. The result is discarded unconditionally: nothing
            // about "does it happen to verify" may ever influence what this
            // branch does next.
            _ = _passwordHasher.Verify(DummyPasswordHash, request.Password);

            // No email logged, unlike the CLAUDE.md §4.4 violation this used
            // to be the one exception to — every other branch here already
            // logs only an id, never a submitted credential.
            _logger.LogWarning("Login failed: no user for the submitted email");
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
        var (tokens, _) = _tokenIssuer.Issue(found.User, familyId: Guid.NewGuid());
        await _refreshTokens.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Login succeeded for {UserId}", found.User.Id);

        // Mapped rather than returned straight through: TokenIssuer's output is an
        // internal shape shared with rotation, and this endpoint owns its own
        // response contract (see LoginCommandResponse).
        return new LoginCommandResponse(tokens.AccessToken, tokens.ExpiresIn, tokens.RefreshToken);
    }

    private static AuthenticationException InvalidCredentials() =>
        new(AuthenticationFailureReason.InvalidCredentials, "Invalid email or password.");
}
