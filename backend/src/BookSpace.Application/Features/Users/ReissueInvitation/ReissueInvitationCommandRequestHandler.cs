using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Users.CreateUser;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Users.ReissueInvitation;

// Hardening pass, 2026-09-25 (finding 3). Four things happen, in an order that
// matters, mirroring CreateUserCommandRequestHandler:
//   1. every token still redeemable for this user is superseded,
//   2. a new activation token is issued,
//   3. both are written in **one** save, and
//   4. only then is the new invitation sent.
//
// Superseding before issuing (rather than after) means a crash between the two
// leaves the account with *no* live token rather than *two* — recoverable by
// calling this again, never silently double-credentialed.
public sealed class ReissueInvitationCommandRequestHandler
    : IRequestHandler<ReissueInvitationCommandRequest, ReissueInvitationCommandResponse>
{
    private readonly IUserRepository _users;
    private readonly IActivationTokenRepository _activationTokens;
    private readonly IActivationTokenFactory _activationTokenFactory;
    private readonly IActivationLinkBuilder _activationLinks;
    private readonly IEmailSender _emailSender;
    private readonly IClock _clock;
    private readonly ILogger<ReissueInvitationCommandRequestHandler> _logger;

    public ReissueInvitationCommandRequestHandler(
        IUserRepository users,
        IActivationTokenRepository activationTokens,
        IActivationTokenFactory activationTokenFactory,
        IActivationLinkBuilder activationLinks,
        IEmailSender emailSender,
        IClock clock,
        ILogger<ReissueInvitationCommandRequestHandler> logger)
    {
        _users = users;
        _activationTokens = activationTokens;
        _activationTokenFactory = activationTokenFactory;
        _activationLinks = activationLinks;
        _emailSender = emailSender;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReissueInvitationCommandResponse> Handle(
        ReissueInvitationCommandRequest request,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        // Tenant-filtered like every other write here, so another tenant's
        // real id arrives as null and leaves as UserNotFound (AC-4).
        var user = await _users.FindForUpdateAsync(request.UserId, cancellationToken)
            ?? throw new UserNotFoundException(request.UserId);

        // Checked before IsActive, deliberately: once an account has ever set
        // its own password, resending an invitation is never the right answer
        // regardless of whether it is currently active — the fix for "I
        // deactivated someone who had already signed in" is reactivation, not
        // a new activation link they no longer need.
        if (await _activationTokens.HasEverBeenConsumedAsync(user.Id, cancellationToken))
        {
            throw new UserAlreadyActivatedException(user.Id);
        }

        // A fresh link would still refuse to redeem — ActivateAccountCommandRequestHandler
        // already checks IsActive (FR-2.4) — so sending one would be a link
        // that looks live and is not. Reactivate first.
        if (!user.IsActive)
        {
            throw new UserNotActiveException(user.Id);
        }

        // Every still-redeemable token is superseded, whether there is one,
        // several (should not happen today, but nothing here assumes it), or
        // none (the original expired or was never sent) — this call is safe to
        // repeat unconditionally. SupersededAtUtc is a concurrency token
        // alongside ConsumedAtUtc (ActivationTokenConfiguration), so a request
        // racing an activation of the very token being superseded is decided
        // by the database, not by whichever handler happened to read first.
        var stillLive = await _activationTokens.FindRedeemableForUserAsync(user.Id, now, cancellationToken);
        foreach (var token in stillLive)
        {
            token.Supersede(now);
        }

        var generated = _activationTokenFactory.Create();
        var expiresAtUtc = now.Add(_activationTokenFactory.Lifetime);

        _activationTokens.Add(new ActivationToken(Guid.NewGuid(), user.Id, generated.Hash, now, expiresAtUtc));

        // One save for the supersede(s) and the new token together — a partial
        // write here would leave either two live tokens or (if the insert is
        // what failed) none, and atomicity is what a single SaveChangesAsync
        // buys across both.
        await _users.SaveChangesAsync(cancellationToken);

        var activationLink = _activationLinks.BuildFor(generated.RawToken);

        // CancellationToken.None, not the caller's token — same reasoning as
        // CreateUserCommandRequestHandler (hardening pass, finding 4): the
        // token is already durably committed, and an aborted HTTP request must
        // not cut a send short when there is no raw link left in the response
        // for the admin to fall back on if it does.
        var sendResult = await _emailSender.SendAsync(
            InvitationEmail.For(user.Email, user.FullName, activationLink, expiresAtUtc),
            CancellationToken.None);

        if (sendResult.Delivered)
        {
            _logger.LogInformation("Invitation reissued for user {UserId}", user.Id);
        }
        else
        {
            _logger.LogWarning(
                "Invitation reissued for user {UserId} but the email did not send", user.Id);
        }

        return new ReissueInvitationCommandResponse(
            user.Id,
            user.Email,
            user.FullName,
            expiresAtUtc,
            sendResult.Delivered);
    }
}
