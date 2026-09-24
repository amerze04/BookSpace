using BookSpace.Application.Abstractions;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Authentication.Activate;

// Redeems an invitation token and sets the account's first password.
//
// FR-2.3 (the password is only ever stored as a hash) and FR-2.4 (a deactivated
// user or suspended organization cannot obtain access) — the second applies here
// as much as it does to login, because an invitation can outlive the decision to
// invite.
//
// **Every failure below reports the same reason code, and that is the design.**
// Expired, already redeemed, no such token, user since deleted, user
// deactivated, organization suspended: one answer. A response that told them
// apart would turn this anonymous endpoint into an oracle for which invitations
// are outstanding and which accounts exist — the concern decision `0018` took
// seriously enough to collapse three distinct approver-ineligibility reasons
// into one code (AC-4). The server log carries the real cause; the client does
// not. See docs/user-management-plan.md §4.2.
//
// Unlike the login handler, there is no dummy-work branch to equalize timing.
// Login needs one because it hashes a *submitted* password only on the
// real-account path; here the PBKDF2 hash is computed before the token is even
// looked up, so every path — valid, spent, expired, unknown — pays the same
// cost in the same order.
public sealed class ActivateAccountCommandRequestHandler
    : IRequestHandler<ActivateAccountCommandRequest, Unit>
{
    private readonly IActivationTokenRepository _activationTokens;
    private readonly IActivationTokenFactory _activationTokenFactory;
    private readonly IAuthenticationUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;
    private readonly ILogger<ActivateAccountCommandRequestHandler> _logger;

    public ActivateAccountCommandRequestHandler(
        IActivationTokenRepository activationTokens,
        IActivationTokenFactory activationTokenFactory,
        IAuthenticationUserRepository users,
        IPasswordHasher passwordHasher,
        IClock clock,
        ILogger<ActivateAccountCommandRequestHandler> logger)
    {
        _activationTokens = activationTokens;
        _activationTokenFactory = activationTokenFactory;
        _users = users;
        _passwordHasher = passwordHasher;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Unit> Handle(ActivateAccountCommandRequest request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // Before the lookup, deliberately — see the class comment. It also means
        // the hash is ready and the window between validating the token and
        // consuming it is as short as it can be.
        var passwordHash = _passwordHasher.Hash(request.Password);

        var presentedHash = _activationTokenFactory.HashOf(request.Token);
        var token = await _activationTokens.FindByHashAsync(presentedHash, cancellationToken);

        if (token is null)
        {
            _logger.LogWarning("Activation failed: no token matches the presented hash");
            throw InvalidToken();
        }

        if (token.IsConsumed)
        {
            _logger.LogWarning(
                "Activation failed: token {TokenId} for user {UserId} was already consumed at {ConsumedAtUtc}",
                token.Id,
                token.UserId,
                token.ConsumedAtUtc);
            throw InvalidToken();
        }

        if (token.HasExpired(now))
        {
            // Not consumed — the row is left exactly as it is. There is nothing
            // to protect against here (an expired token is already unusable) and
            // marking it would destroy the distinction between "never redeemed"
            // and "redeemed", which is the only thing this table can tell an
            // administrator afterwards.
            _logger.LogWarning(
                "Activation failed: token {TokenId} for user {UserId} expired at {ExpiresAtUtc}",
                token.Id,
                token.UserId,
                token.ExpiresAtUtc);
            throw InvalidToken();
        }

        var found = await _users.FindByIdAsync(token.UserId, cancellationToken);

        // FR-2.4. An invitation can outlive the decision to invite: the admin may
        // have deactivated the account, or the whole organization may have been
        // suspended, between sending the email and the recipient clicking it.
        // Null status = SysAdmin, who has no organization to be suspended.
        if (found is null
            || !found.User.IsActive
            || found.OrganizationStatus is OrganizationStatus.Suspended)
        {
            _logger.LogWarning(
                "Activation failed: user {UserId} is not eligible to activate", token.UserId);
            throw InvalidToken();
        }

        // The user acts on their own account — the one moment in this system
        // where the actor is somebody who has never signed in.
        found.User.SetPassword(passwordHash, actorUserId: found.User.Id, nowUtc: now);
        token.Consume(now);

        // One SaveChangesAsync for both, so the implicit transaction covers the
        // pair. Splitting them has no safe order: a crash after the password
        // write leaves a live token somebody else could redeem, and a crash
        // after consuming leaves an account with no password and — since there
        // is no way to re-issue an invitation (docs/user-management-plan.md §6)
        // — no route in at all.
        //
        // Unfiltered because this request is anonymous: with no tenant context,
        // row-level security cannot see the Users row and the UPDATE would
        // affect zero rows. See IAuthenticationUserRepository.
        //
        // ConsumedAtUtc is a concurrency token, so two requests redeeming the
        // same token at once do not both succeed — the loser affects zero rows
        // and surfaces as a DbUpdateConcurrencyException (409), which is what
        // makes "single use" true under concurrency rather than only in
        // sequence. See ActivationTokenConfiguration.
        await _users.SaveChangesUnfilteredAsync(cancellationToken);

        _logger.LogInformation("Account activated for user {UserId}", found.User.Id);

        return Unit.Value;
    }

    private static AuthenticationException InvalidToken() =>
        new(
            AuthenticationFailureReason.InvalidActivationToken,
            "The activation link is not valid, has expired, or has already been used.");
}
