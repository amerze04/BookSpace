using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Authentication;
using BookSpace.Application.Features.Authentication.Activate;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookSpace.UnitTests.Authentication;

// Account activation's decision table (user management phase 2).
//
// The important property is not that the happy path works — it is that **every
// other path answers identically**. Expired, already redeemed, unknown,
// deactivated user, suspended organization: one reason code, one status. A test
// per branch, all asserting the same code, is what stops somebody later adding
// a helpful "this link has expired" and turning the endpoint into an oracle for
// which invitations are outstanding (docs/user-management-plan.md §4.2).
public class ActivateAccountCommandRequestHandlerTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private const string NewPassword = "a-long-enough-password";

    private readonly FakeActivationTokenRepository _activationTokens = new();
    private readonly FakeActivationTokenFactory _activationTokenFactory = new();
    private readonly FakeAuthenticationUserRepository _users = new();
    private readonly RecordingPasswordHasher _passwordHasher = new();
    private readonly TestClock _clock = new(NowUtc);

    [Fact]
    public async Task AValidToken_SetsThePassword()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user);

        await Handler().Handle(new ActivateAccountCommandRequest(raw, NewPassword), CancellationToken.None);

        Assert.Equal($"hash::{NewPassword}", user.PasswordHash);
    }

    [Fact]
    public async Task AValidToken_IsConsumed()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user);

        await Handler().Handle(new ActivateAccountCommandRequest(raw, NewPassword), CancellationToken.None);

        var token = Assert.Single(_activationTokens.Tokens);
        Assert.True(token.IsConsumed);
        Assert.Equal(NowUtc, token.ConsumedAtUtc);
    }

    // The atomicity claim, asserted rather than described: the password write
    // and the token consumption go in one save. Two saves would mean a crash
    // between them could leave either a live token on an activated account or
    // an account with no password and no way to get one.
    [Fact]
    public async Task AValidToken_SavesOnce()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user);

        await Handler().Handle(new ActivateAccountCommandRequest(raw, NewPassword), CancellationToken.None);

        Assert.Equal(1, _users.SaveCount);
    }

    [Fact]
    public async Task AnUnknownToken_IsRefused()
    {
        ProvisionedUser();

        await AssertRefused("a-token-nobody-ever-issued");
    }

    [Fact]
    public async Task AnAlreadyConsumedToken_IsRefused()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user);
        _activationTokens.Tokens.Single().Consume(NowUtc.AddMinutes(-1));

        await AssertRefused(raw);
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user, expiresAtUtc: NowUtc.AddMinutes(-1));

        await AssertRefused(raw);
    }

    // FR-2.4. An invitation can outlive the decision to invite.
    [Fact]
    public async Task ATokenForADeactivatedUser_IsRefused()
    {
        var user = ProvisionedUser();
        user.Deactivate(Guid.NewGuid(), NowUtc.AddMinutes(-1));
        var raw = IssueTokenFor(user);

        await AssertRefused(raw);
    }

    [Fact]
    public async Task ATokenForASuspendedOrganization_IsRefused()
    {
        var user = ProvisionedUser(OrganizationStatus.Suspended);
        var raw = IssueTokenFor(user);

        await AssertRefused(raw);
    }

    [Fact]
    public async Task ATokenForAUserThatNoLongerExists_IsRefused()
    {
        // Issued against an id the user repository has never heard of.
        var raw = IssueTokenFor(userId: Guid.NewGuid());

        await AssertRefused(raw);
    }

    // Hardening pass, 2026-09-25 (findings 2/3). An administrator resent the
    // invitation while this one was still outstanding — only the newer link
    // may work.
    [Fact]
    public async Task ASupersededToken_IsRefused()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user);
        _activationTokens.Tokens[^1].Supersede(NowUtc.AddMinutes(-1));

        await AssertRefused(raw);
    }

    // Left exactly as superseded — a redemption attempt against a replaced
    // link must not retroactively mark it consumed, which would misreport
    // that the recipient actually got in on this one.
    [Fact]
    public async Task ASupersededToken_IsNotMarkedConsumed()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user);
        _activationTokens.Tokens[^1].Supersede(NowUtc.AddMinutes(-1));

        await AssertRefused(raw);

        Assert.False(_activationTokens.Tokens.Single().IsConsumed);
    }

    // The point of the whole design, stated once: all five refusals are the
    // same answer. If this fails, some branch started being helpful.
    [Fact]
    public async Task EveryRefusal_ReportsTheSameCodeAndKind()
    {
        var reasons = new List<string>();
        var kinds = new List<ErrorKind>();

        foreach (var raw in await EveryRefusableToken())
        {
            var exception = await Assert.ThrowsAsync<AuthenticationException>(() =>
                Handler().Handle(new ActivateAccountCommandRequest(raw, NewPassword), CancellationToken.None));

            reasons.Add(exception.ReasonCode);
            kinds.Add(exception.Kind);
        }

        Assert.Equal(6, reasons.Count);
        Assert.All(reasons, r => Assert.Equal(AuthenticationFailureReason.InvalidActivationToken, r));
        Assert.All(kinds, k => Assert.Equal(ErrorKind.Unauthorized, k));
    }

    // Nothing is written on any refusal — in particular an expired or unknown
    // token must not leave the account half-changed.
    [Fact]
    public async Task ARefusal_WritesNothing()
    {
        var user = ProvisionedUser();
        var originalHash = user.PasswordHash;
        var raw = IssueTokenFor(user, expiresAtUtc: NowUtc.AddMinutes(-1));

        await AssertRefused(raw);

        Assert.Equal(originalHash, user.PasswordHash);
        Assert.Equal(0, _users.SaveCount);
    }

    // An expired token is left unconsumed deliberately. It is already unusable,
    // and marking it would destroy the only thing this table can tell an
    // administrator afterwards: whether the invitation was ever redeemed.
    [Fact]
    public async Task AnExpiredToken_IsNotMarkedConsumed()
    {
        var user = ProvisionedUser();
        var raw = IssueTokenFor(user, expiresAtUtc: NowUtc.AddMinutes(-1));

        await AssertRefused(raw);

        Assert.False(_activationTokens.Tokens.Single().IsConsumed);
    }

    // Unlike login, this handler needs no dummy-work branch to equalize timing,
    // because the expensive hash is computed before the token is looked up at
    // all. Asserting it directly, since the ordering is the whole mitigation
    // and nothing else in the code says so.
    [Fact]
    public async Task ThePasswordIsHashedEvenWhenTheTokenIsUnknown()
    {
        await AssertRefused("a-token-nobody-ever-issued");

        Assert.Contains($"hash::{NewPassword}", _passwordHasher.Hashed);
    }

    private async Task<IReadOnlyList<string>> EveryRefusableToken()
    {
        var tokens = new List<string> { "a-token-nobody-ever-issued" };

        var consumed = ProvisionedUser();
        tokens.Add(IssueTokenFor(consumed));
        _activationTokens.Tokens[^1].Consume(NowUtc.AddMinutes(-1));

        var expiredFor = ProvisionedUser();
        tokens.Add(IssueTokenFor(expiredFor, expiresAtUtc: NowUtc.AddMinutes(-1)));

        var deactivated = ProvisionedUser();
        deactivated.Deactivate(Guid.NewGuid(), NowUtc.AddMinutes(-1));
        tokens.Add(IssueTokenFor(deactivated));

        var suspended = ProvisionedUser(OrganizationStatus.Suspended);
        tokens.Add(IssueTokenFor(suspended));

        var superseded = ProvisionedUser();
        tokens.Add(IssueTokenFor(superseded));
        _activationTokens.Tokens[^1].Supersede(NowUtc.AddMinutes(-1));

        return await Task.FromResult(tokens);
    }

    private async Task AssertRefused(string rawToken)
    {
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() =>
            Handler().Handle(new ActivateAccountCommandRequest(rawToken, NewPassword), CancellationToken.None));

        Assert.Equal(AuthenticationFailureReason.InvalidActivationToken, exception.ReasonCode);
        Assert.Equal(ErrorKind.Unauthorized, exception.Kind);
    }

    private ActivateAccountCommandRequestHandler Handler() =>
        new(
            _activationTokens,
            _activationTokenFactory,
            _users,
            _passwordHasher,
            _clock,
            NullLogger<ActivateAccountCommandRequestHandler>.Instance);

    private User ProvisionedUser(OrganizationStatus status = OrganizationStatus.Active)
    {
        // The placeholder credential phase 3 will create a user with: a hash of
        // something nobody knows, so the account cannot be signed into until it
        // is activated.
        var user = new User(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"invitee-{Guid.NewGuid():N}@acme.test",
            "placeholder-hash",
            "Invited Person",
            Guid.NewGuid(),
            NowUtc.AddDays(-1));

        _users.Add(user, status);
        return user;
    }

    private string IssueTokenFor(User user, DateTime? expiresAtUtc = null) =>
        IssueTokenFor(user.Id, expiresAtUtc);

    private string IssueTokenFor(Guid userId, DateTime? expiresAtUtc = null)
    {
        var generated = _activationTokenFactory.Create();
        var issuedAt = NowUtc.AddDays(-1);

        _activationTokens.Add(new ActivationToken(
            Guid.NewGuid(),
            userId,
            generated.Hash,
            issuedAt,
            expiresAtUtc ?? issuedAt.Add(_activationTokenFactory.Lifetime)));

        return generated.RawToken;
    }
}
