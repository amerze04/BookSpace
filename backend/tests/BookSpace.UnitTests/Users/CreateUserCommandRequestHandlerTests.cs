using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Users.CreateUser;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Authentication;
using BookSpace.UnitTests.Persistence;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookSpace.UnitTests.Users;

// POST /users' handler. The ordering rules are what this file is mostly about —
// the account is written before anything is emailed, and the account, its role
// and its activation token are written together — because both are invisible in
// a green happy-path test and expensive to get wrong.
public class CreateUserCommandRequestHandlerTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 24, 11, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();

    private readonly FakeUserRepository _users = new();
    private readonly FakeActivationTokenRepository _activationTokens = new();
    private readonly FakeActivationTokenFactory _activationTokenFactory = new();
    private readonly StubActivationLinkBuilder _links = new();
    private readonly RecordingEmailSender _emails = new();
    private readonly RecordingPasswordHasher _passwordHasher = new();
    private readonly TestClock _clock = new(NowUtc);

    [Fact]
    public async Task ItCreatesTheUserInTheCallersTenant()
    {
        var response = await Handle();

        var created = Assert.Single(_users.Added);
        Assert.Equal(OrgId, created.OrgId);
        Assert.Equal("ada@acme.test", created.Email);
        Assert.Equal("Ada Lovelace", created.FullName);
        Assert.Equal(response.Id, created.Id);
    }

    // The provisioning audit trail: Users.CreatedByUserId is not nullable, and
    // who added a colleague is the whole point of recording it.
    [Fact]
    public async Task ItRecordsTheInvitingAdministrator()
    {
        await Handle();

        var created = Assert.Single(_users.Added);
        Assert.Equal(AdminId, created.CreatedByUserId);
        Assert.Equal(NowUtc, created.CreatedAtUtc);
    }

    [Fact]
    public async Task TheNewAccountIsActive()
    {
        var response = await Handle();

        Assert.True(response.IsActive);
        Assert.True(Assert.Single(_users.Added).IsActive);
    }

    // A phase-3 call, and a correction to the plan's premise that a roleless
    // user "can sign in and see nothing" — AuthorizationPolicies.TenantMember
    // requires only the orgId claim, so they could already browse and book.
    // Member grants nothing beyond that and makes the row describe what the
    // account can actually do.
    [Fact]
    public async Task TheNewUserIsAMember()
    {
        var response = await Handle();

        Assert.Equal([Role.Member], Assert.Single(_users.Added).Roles);
        Assert.Equal([Role.Member], response.Roles);
    }

    // Not Approver, not TenantAdmin: those grant something, and granting them
    // is phase 5's job, where the last-TenantAdmin guard lives.
    [Fact]
    public async Task TheNewUserGetsNoPrivilegedRole()
    {
        await Handle();

        var roles = Assert.Single(_users.Added).Roles;
        Assert.DoesNotContain(Role.Approver, roles);
        Assert.DoesNotContain(Role.TenantAdmin, roles);
        Assert.DoesNotContain(Role.SysAdmin, roles);
    }

    // There must be no window in which a provisioned account is sign-innable
    // before its owner activates it, so the placeholder is a hash of something
    // nobody — including the administrator — ever sees.
    [Fact]
    public async Task ThePlaceholderPasswordIsUnguessable()
    {
        await Handle();
        var first = Assert.Single(_users.Added).PasswordHash;

        _users.Added.Clear();
        await Handle();
        var second = Assert.Single(_users.Added).PasswordHash;

        Assert.NotEqual(first, second);
        Assert.False(string.IsNullOrWhiteSpace(first));
        // Whatever was hashed, it was not anything the caller supplied.
        Assert.DoesNotContain("ada@acme.test", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ada", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ItIssuesAnActivationTokenForTheNewUser()
    {
        var response = await Handle();

        var token = Assert.Single(_activationTokens.Tokens);
        Assert.Equal(response.Id, token.UserId);
        Assert.Equal(NowUtc, token.IssuedAtUtc);
        Assert.False(token.IsConsumed);
    }

    [Fact]
    public async Task TheTokenExpiresAfterTheConfiguredLifetime()
    {
        _activationTokenFactory.Lifetime = TimeSpan.FromDays(3);

        var response = await Handle();

        Assert.Equal(NowUtc.AddDays(3), Assert.Single(_activationTokens.Tokens).ExpiresAtUtc);
        Assert.Equal(NowUtc.AddDays(3), response.ActivationLinkExpiresAtUtc);
    }

    // Only the hash is stored; the plaintext exists in the email and nowhere
    // else — not even in this response any more (hardening pass, 2026-09-25,
    // finding 2; FR-2.3's shape, decision `0011`).
    [Fact]
    public async Task OnlyTheHashOfTheTokenIsStored()
    {
        await Handle();

        var stored = Assert.Single(_activationTokens.Tokens);
        Assert.NotEqual(stored.TokenHash, _links.LastRawToken);
    }

    // The atomicity claim: the account, its role and its token go in one save.
    [Fact]
    public async Task EverythingIsWrittenInOneSave()
    {
        await Handle();

        Assert.Equal(1, _users.SaveCount);
    }

    // **The ordering rule.** An email that says "click here" must never go out
    // for an account the database refused.
    [Fact]
    public async Task ARefusedInsertSendsNoEmail()
    {
        _users.NextSaveHitsDuplicateEmail = true;

        await Assert.ThrowsAsync<EmailAlreadyInUseException>(Handle);

        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task ADuplicateEmailIsReportedAsAConflict()
    {
        _users.NextSaveHitsDuplicateEmail = true;

        var exception = await Assert.ThrowsAsync<EmailAlreadyInUseException>(Handle);

        Assert.Equal(ReasonCodes.EmailAlreadyInUse, exception.ReasonCode);
        Assert.Equal(ErrorKind.Conflict, exception.Kind);
    }

    // Decision `0010` made email unique platform-wide, so the collision may be
    // with an account in an organization this admin cannot see. Nothing in the
    // refusal may hint at which (AC-4, docs/user-management-plan.md §3.2).
    [Fact]
    public async Task ADuplicateEmailRefusalNamesNobody()
    {
        _users.NextSaveHitsDuplicateEmail = true;

        var exception = await Assert.ThrowsAsync<EmailAlreadyInUseException>(Handle);

        Assert.DoesNotContain("ada@acme.test", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(exception.Extensions);
    }

    [Fact]
    public async Task ItSendsTheInvitationToTheNewUser()
    {
        await Handle();

        var sent = Assert.Single(_emails.Sent);
        Assert.Equal("ada@acme.test", sent.To.Address);
        Assert.Equal("Ada Lovelace", sent.To.DisplayName);
        Assert.Contains(_links.LastBuiltLink!, sent.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulSendIsReported()
    {
        var response = await Handle();

        Assert.True(response.InvitationEmailSent);
    }

    // §4.3: creating a colleague must not fail because a third party is down.
    // The account is real and the admin is told the email did not go out.
    //
    // **No link comes back any more (hardening pass, 2026-09-25, finding 2).**
    // The recovery path is `POST /users/{id}/invitation`
    // (ReissueInvitationCommandRequestHandler) — the account is not stranded,
    // it is just not recoverable from *this* response.
    [Fact]
    public async Task AFailedSendStillCreatesTheUserButHandsBackNoLink()
    {
        _emails.NextSendFails = true;

        var response = await Handle();

        Assert.False(response.InvitationEmailSent);
        Assert.Single(_users.Added);
        Assert.Single(_activationTokens.Tokens);
    }

    // The provider's own words can name hosts and accounts (see IEmailSender),
    // so they stay in the log. Asserted over the serialized response rather
    // than field by field, so adding a field later cannot quietly reopen this.
    [Fact]
    public async Task AFailedSendDoesNotLeakTheProvidersDetailIntoTheResponse()
    {
        _emails.NextSendFails = true;
        _emails.FailureDetail = "SmtpCommandException: 535 auth failed for apikey@smtp.internal";

        var response = await Handle();
        var serialized = System.Text.Json.JsonSerializer.Serialize(response);

        Assert.DoesNotContain("smtp.internal", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SmtpCommandException", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("535", serialized, StringComparison.Ordinal);
    }

    // Both come from the token, never the payload. A missing one is a wiring
    // bug behind two stacked policies, so it is a 500 rather than a reason code
    // — the same call CreateResourceCommandRequestHandler makes.
    [Fact]
    public async Task NoTenantContextIsAWiringBug()
    {
        var handler = Handler(tenant: new FixedCurrentTenant(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task NoAuthenticatedUserIsAWiringBug()
    {
        var handler = Handler(currentUser: new FixedCurrentUser(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(Request(), CancellationToken.None));
    }

    // Nothing is written when the wiring is broken either — a 500 that had
    // already inserted half an account would be worse than the 500.
    [Fact]
    public async Task AWiringBugWritesNothingAndSendsNothing()
    {
        var handler = Handler(tenant: new FixedCurrentTenant(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.Handle(Request(), CancellationToken.None));

        Assert.Empty(_users.Added);
        Assert.Empty(_activationTokens.Tokens);
        Assert.Empty(_emails.Sent);
        Assert.Equal(0, _users.SaveCount);
    }

    private Task<CreateUserCommandResponse> Handle() =>
        Handler().Handle(Request(), CancellationToken.None);

    private static CreateUserCommandRequest Request(
        string email = "ada@acme.test",
        string fullName = "Ada Lovelace") => new(email, fullName);

    private CreateUserCommandRequestHandler Handler(
        ICurrentTenant? tenant = null,
        ICurrentUser? currentUser = null) =>
        new(
            _users,
            _activationTokens,
            _activationTokenFactory,
            _links,
            _emails,
            _passwordHasher,
            tenant ?? new FixedCurrentTenant(OrgId),
            currentUser ?? new FixedCurrentUser(AdminId),
            _clock,
            NullLogger<CreateUserCommandRequestHandler>.Instance);

    // ---- Hardening pass, 2026-09-25 ----

    // Finding 2: the response no longer hands the admin a raw activation link
    // — the whole point of the fix — so the only fallback when delivery fails
    // is to check the response's own JSON shape rather than trust the type
    // system (which would just fail to compile if a field crept back in). A
    // future contributor cannot restore ActivationLink to the record and slip
    // it past review without this test naming the fact loudly.
    [Fact]
    public async Task TheResponseNeverCarriesTheRawActivationLink()
    {
        var response = await Handle();

        var serialized = System.Text.Json.JsonSerializer.Serialize(response);

        // The property key, not a loose substring match — "ActivationLink" is
        // also a prefix of the (still legitimate) "ActivationLinkExpiresAtUtc".
        Assert.DoesNotContain("\"ActivationLink\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_links.LastRawToken!, serialized, StringComparison.Ordinal);
    }

    // Finding 4: the account and its token are already durably committed by
    // the time the invitation is sent, so a request that is cancelled after
    // that point — the administrator's browser navigating away, a proxy
    // timing out — must not cut the send short. Passing an already-cancelled
    // token into Handle() is the deterministic way to prove it: if the send
    // still used the caller's token, this would either throw
    // OperationCanceledException or never call the sender at all. It does
    // neither — the whole request completes, and the sender can show it was
    // never given a cancelled token to begin with.
    [Fact]
    public async Task ARequestCancelledBeforeTheSendStepStillDeliversTheInvitation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await Handler().Handle(Request(), cts.Token);

        Assert.True(response.InvitationEmailSent);
        Assert.Single(_emails.Sent);
        Assert.NotNull(_emails.LastReceivedToken);
        Assert.False(_emails.LastReceivedToken!.Value.IsCancellationRequested);
    }

    private sealed class StubActivationLinkBuilder : IActivationLinkBuilder
    {
        public string? LastRawToken { get; private set; }

        public string? LastBuiltLink { get; private set; }

        public string BuildFor(string rawToken)
        {
            LastRawToken = rawToken;
            LastBuiltLink = $"https://bookspace.test/activate?token={rawToken}";
            return LastBuiltLink;
        }
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public bool NextSendFails { get; set; }

        public string FailureDetail { get; set; } = "the provider refused";

        // Hardening pass, 2026-09-25 (finding 4) — what the handler actually
        // passed, so a test can assert it is not the caller's own (possibly
        // cancelled) token.
        public CancellationToken? LastReceivedToken { get; private set; }

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            LastReceivedToken = cancellationToken;

            if (NextSendFails)
            {
                // Recorded as attempted but not delivered, which is what the
                // real senders do — a failure is a return value, never a throw.
                return Task.FromResult(EmailSendResult.Failed(FailureDetail));
            }

            Sent.Add(message);
            return Task.FromResult(EmailSendResult.Success());
        }
    }
}
