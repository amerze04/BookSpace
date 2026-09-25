using BookSpace.Application.Abstractions;
using BookSpace.Application.Messaging;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Users.CreateUser;

// Provisions a colleague and invites them. PRD §2's Tenant Administrator
// persona ("manage ... members, roles"); there is no FR to cite, and
// docs/user-management-plan.md §1 says so rather than inventing one.
//
// Four things happen, in an order that matters:
//   1. the account is created with an unguessable placeholder credential,
//   2. an activation token is issued for it,
//   3. both are written in **one** save, and
//   4. only then is the invitation sent.
//
// Sending last is what makes an email that says "click here" true: nothing is
// mailed for an account the database refused. And the single save is what stops
// an account existing with no way into it, or a live token pointing at no
// account.
public sealed class CreateUserCommandRequestHandler
    : IRequestHandler<CreateUserCommandRequest, CreateUserCommandResponse>
{
    private readonly IUserRepository _users;
    private readonly IActivationTokenRepository _activationTokens;
    private readonly IActivationTokenFactory _activationTokenFactory;
    private readonly IActivationLinkBuilder _activationLinks;
    private readonly IEmailSender _emailSender;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<CreateUserCommandRequestHandler> _logger;

    public CreateUserCommandRequestHandler(
        IUserRepository users,
        IActivationTokenRepository activationTokens,
        IActivationTokenFactory activationTokenFactory,
        IActivationLinkBuilder activationLinks,
        IEmailSender emailSender,
        IPasswordHasher passwordHasher,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        IClock clock,
        ILogger<CreateUserCommandRequestHandler> logger)
    {
        _users = users;
        _activationTokens = activationTokens;
        _activationTokenFactory = activationTokenFactory;
        _activationLinks = activationLinks;
        _emailSender = emailSender;
        _passwordHasher = passwordHasher;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CreateUserCommandResponse> Handle(
        CreateUserCommandRequest request,
        CancellationToken cancellationToken)
    {
        // Both from the token, never the payload. InvalidOperationException
        // rather than an AppException, matching CreateResourceCommandRequestHandler:
        // this endpoint sits behind TenantAdmin *and* TenantMember's orgId-claim
        // requirement, so a request that got here has both, and null means the
        // wiring is broken — a 500, not a reason code.
        var orgId = _currentTenant.OrgId
            ?? throw new InvalidOperationException(
                "No tenant context: a user cannot be created without an owning organization.");
        var actorUserId = _currentUser.UserId
            ?? throw new InvalidOperationException(
                "No authenticated user: CreatedByUserId is required (the provisioning audit trail).");

        var nowUtc = _clock.UtcNow;

        var user = new User(
            Guid.NewGuid(),
            orgId,
            request.Email,
            PlaceholderPasswordHash(),
            request.FullName,
            actorUserId,
            nowUtc);

        // **Member, not "no roles at all"** — a phase-3 call, and a correction to
        // the plan's own premise. AuthorizationPolicies.TenantMember requires
        // only the orgId claim, so a roleless user is not somebody who "can sign
        // in and see nothing": they can already browse and book exactly as a
        // Member can. Nothing in the application branches on Role.Member — it is
        // purely descriptive — so assigning it grants nothing extra and makes the
        // row describe what the account can actually do. It also keeps a
        // provisioned user structurally identical to a seeded one, which gives
        // Member explicitly.
        //
        // Not Approver or TenantAdmin: those do grant something, and granting
        // them is phase 5's job, where the last-TenantAdmin guard lives.
        user.AddRole(Role.Member, actorUserId, nowUtc);

        var generated = _activationTokenFactory.Create();
        var expiresAtUtc = nowUtc.Add(_activationTokenFactory.Lifetime);

        _activationTokens.Add(new ActivationToken(
            Guid.NewGuid(),
            user.Id,
            generated.Hash,
            nowUtc,
            expiresAtUtc));

        _users.Add(user);

        // One save for the account, its role and its token — and the only place
        // a duplicate email is refused, as EmailAlreadyInUseException. See
        // IUserRepository.SaveChangesAsync: there is no pre-check, so this path
        // never learns which tenant a colliding address belongs to.
        await _users.SaveChangesAsync(cancellationToken);

        var activationLink = _activationLinks.BuildFor(generated.RawToken);

        // After the save, deliberately. A send that raced ahead of a refused
        // insert would invite somebody to an account that does not exist.
        //
        // **CancellationToken.None, not the caller's token (hardening pass,
        // 2026-09-25, finding 4).** The account and its activation token are
        // already durably committed by this point — SaveChangesAsync above has
        // returned. If the HTTP request is then cancelled (the administrator's
        // browser navigates away, a proxy times out) while this send is still
        // in flight, the caller's token would abort it mid-delivery, and unlike
        // before this hardening pass there is no raw link in the response for
        // the admin to fall back on if that happens — the send has to be given
        // the chance to finish on its own. MailKit still bounds the call with
        // its own socket timeouts regardless of what token it is given (see
        // Infrastructure/Email's own note on this), so nothing here can hang
        // forever; it can only outlive a request that is no longer listening.
        // If delivery still fails, or nobody ever sees the result of this
        // request, ReissueInvitationCommandRequestHandler is the recovery path
        // — the account is never stranded by a send this request cannot wait
        // for.
        var sendResult = await _emailSender.SendAsync(
            InvitationEmail.For(user.Email, user.FullName, activationLink, expiresAtUtc),
            CancellationToken.None);

        if (sendResult.Delivered)
        {
            _logger.LogInformation("Invitation sent for new user {UserId}", user.Id);
        }
        else
        {
            // Warning, not Error, and the request still succeeds: the account is
            // real, and the recovery path is POST /users/{id}/invitation
            // (hardening pass, 2026-09-25, finding 3) — not a raw link handed
            // back here (finding 2). The provider's own detail is logged by the
            // sender; it is not repeated here and never reaches the response,
            // because it can name hosts and accounts.
            _logger.LogWarning(
                "User {UserId} was created but the invitation email did not send; "
                + "an administrator can retry via POST /users/{{id}}/invitation",
                user.Id);
        }

        return new CreateUserCommandResponse(
            user.Id,
            user.Email,
            user.FullName,
            user.IsActive,
            user.Roles.ToList(),
            user.CreatedAtUtc,
            expiresAtUtc,
            sendResult.Delivered);
    }

    // A credential nobody knows, including the administrator who just created
    // the account — so there is no window in which a provisioned user is
    // sign-innable before they activate.
    //
    // Not an empty string: User's constructor refuses one, and it should, since
    // an account with no hash at all is a different kind of broken. Two v4 Guids
    // rather than a call through some crypto port, because ~244 bits of
    // non-sequential randomness is far past what a PBKDF2-hashed value nobody
    // ever transmits needs, and adding a port to the Application layer for a
    // value that is thrown away would buy nothing.
    private string PlaceholderPasswordHash() =>
        _passwordHasher.Hash($"{Guid.NewGuid():N}{Guid.NewGuid():N}");
}
