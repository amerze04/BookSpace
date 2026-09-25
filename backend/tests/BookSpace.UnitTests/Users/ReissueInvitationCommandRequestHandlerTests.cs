using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Users.ReissueInvitation;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Authentication;
using BookSpace.UnitTests.Resources;
using BookSpace.UnitTests.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookSpace.UnitTests.Users;

// Hardening pass, 2026-09-25 (finding 3). POST /users/{id}/invitation — the
// recovery path an expired, lost or never-delivered invitation did not have.
public class ReissueInvitationCommandRequestHandlerTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 25, 11, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private readonly FakeUserRepository _users = new();
    private readonly FakeActivationTokenRepository _activationTokens = new();
    private readonly FakeActivationTokenFactory _activationTokenFactory = new();
    private readonly StubActivationLinkBuilder _links = new();
    private readonly RecordingEmailSender _emails = new();
    private readonly TestClock _clock = new(NowUtc);

    [Fact]
    public async Task ItIssuesANewTokenForTheUser()
    {
        var user = User();

        await Reissue(user.Id);

        var live = Assert.Single(_activationTokens.Tokens, t => t.CanBeRedeemed(NowUtc));
        Assert.Equal(user.Id, live.UserId);
        Assert.Equal(NowUtc, live.IssuedAtUtc);
    }

    [Fact]
    public async Task TheResponseCarriesTheUserAndExpiry()
    {
        var user = User();
        _activationTokenFactory.Lifetime = TimeSpan.FromDays(5);

        var response = await Reissue(user.Id);

        Assert.Equal(user.Id, response.Id);
        Assert.Equal(user.Email, response.Email);
        Assert.Equal(user.FullName, response.FullName);
        Assert.Equal(NowUtc.AddDays(5), response.ActivationLinkExpiresAtUtc);
    }

    // The requirement stated explicitly: never two simultaneously usable
    // credentials for the same account.
    [Fact]
    public async Task ItSupersedesAStillLiveTokenBeforeIssuingTheNewOne()
    {
        var user = User();
        var original = LiveToken(user.Id);

        await Reissue(user.Id);

        Assert.True(original.IsSuperseded);
        Assert.False(original.CanBeRedeemed(NowUtc));
        Assert.Single(_activationTokens.Tokens, t => t.CanBeRedeemed(NowUtc));
    }

    [Fact]
    public async Task ItSupersedesEveryStillLiveTokenNotJustOne()
    {
        var user = User();
        var first = LiveToken(user.Id);
        var second = LiveToken(user.Id);

        await Reissue(user.Id);

        Assert.True(first.IsSuperseded);
        Assert.True(second.IsSuperseded);
    }

    // Already unusable, so nothing to protect — and touching it would erase
    // the one thing this row can still tell an administrator (that it expired
    // rather than being replaced).
    [Fact]
    public async Task AnAlreadyExpiredTokenIsLeftAlone()
    {
        var user = User();
        var expired = new ActivationToken(
            Guid.NewGuid(), user.Id, "expired-hash", NowUtc.AddDays(-10), NowUtc.AddDays(-3));
        _activationTokens.Tokens.Add(expired);

        await Reissue(user.Id);

        Assert.False(expired.IsSuperseded);
    }

    [Fact]
    public async Task AnUnknownUserIsNotFound()
    {
        var exception = await Assert.ThrowsAsync<UserNotFoundException>(() => Reissue(Guid.NewGuid()));

        Assert.Equal(ReasonCodes.UserNotFound, exception.ReasonCode);
        Assert.Equal(ErrorKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task ADeactivatedUserIsRefused()
    {
        var user = User();
        user.Deactivate(ActorId, NowUtc);

        var exception = await Assert.ThrowsAsync<UserNotActiveException>(() => Reissue(user.Id));

        Assert.Equal(ReasonCodes.UserNotActive, exception.ReasonCode);
        Assert.Equal(ErrorKind.RuleViolation, exception.Kind);
    }

    [Fact]
    public async Task ADeactivatedUserGetsNoNewToken()
    {
        var user = User();
        user.Deactivate(ActorId, NowUtc);

        await Assert.ThrowsAsync<UserNotActiveException>(() => Reissue(user.Id));

        Assert.Empty(_activationTokens.Tokens);
        Assert.Empty(_emails.Sent);
    }

    [Fact]
    public async Task AnAlreadyActivatedUserIsRefused()
    {
        var user = User();
        var consumed = new ActivationToken(
            Guid.NewGuid(), user.Id, "consumed-hash", NowUtc.AddDays(-3), NowUtc.AddDays(4));
        consumed.Consume(NowUtc.AddDays(-2));
        _activationTokens.Tokens.Add(consumed);

        var exception = await Assert.ThrowsAsync<UserAlreadyActivatedException>(() => Reissue(user.Id));

        Assert.Equal(ReasonCodes.UserAlreadyActivated, exception.ReasonCode);
        Assert.Equal(ErrorKind.Conflict, exception.Kind);
    }

    // Checked ahead of the active-status refusal: once truly activated, the
    // answer never becomes "reactivate and I'll resend it" even if the account
    // is currently deactivated too.
    [Fact]
    public async Task AlreadyActivatedOutranksDeactivatedInTheRefusal()
    {
        var user = User();
        user.Deactivate(ActorId, NowUtc);
        var consumed = new ActivationToken(
            Guid.NewGuid(), user.Id, "consumed-hash", NowUtc.AddDays(-3), NowUtc.AddDays(4));
        consumed.Consume(NowUtc.AddDays(-2));
        _activationTokens.Tokens.Add(consumed);

        await Assert.ThrowsAsync<UserAlreadyActivatedException>(() => Reissue(user.Id));
    }

    [Fact]
    public async Task ItSendsTheNewInvitation()
    {
        var user = User();

        await Reissue(user.Id);

        var sent = Assert.Single(_emails.Sent);
        Assert.Equal(user.Email, sent.To.Address);
        Assert.Contains(_links.LastBuiltLink!, sent.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulSendIsReported()
    {
        var response = await Reissue(User().Id);

        Assert.True(response.InvitationEmailSent);
    }

    // §4.3's rule extends here: a delivery failure still leaves the account
    // reachable — the new token exists and can be redeemed, or reissued again.
    [Fact]
    public async Task AFailedSendStillLeavesANewRedeemableToken()
    {
        _emails.NextSendFails = true;
        var user = User();

        var response = await Reissue(user.Id);

        Assert.False(response.InvitationEmailSent);
        Assert.Single(_activationTokens.Tokens, t => t.CanBeRedeemed(NowUtc));
    }

    // Same fix as CreateUserCommandRequestHandler, and for the same reason:
    // the token is durably committed by the time the send happens, so an
    // aborted request must not cut delivery short.
    [Fact]
    public async Task ARequestCancelledBeforeTheSendStepStillDeliversTheInvitation()
    {
        var user = User();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await Handler().Handle(new ReissueInvitationCommandRequest(user.Id), cts.Token);

        Assert.True(response.InvitationEmailSent);
        Assert.NotNull(_emails.LastReceivedToken);
        Assert.False(_emails.LastReceivedToken!.Value.IsCancellationRequested);
    }

    // Mirrors CreateUserCommandRequestHandlerTests' own regression guard: this
    // response must never regain a raw credential either.
    [Fact]
    public async Task TheResponseNeverCarriesARawActivationLink()
    {
        var response = await Reissue(User().Id);

        var serialized = System.Text.Json.JsonSerializer.Serialize(response);

        Assert.DoesNotContain("\"ActivationLink\"", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_links.LastRawToken!, serialized, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private Task<ReissueInvitationCommandResponse> Reissue(Guid userId) =>
        Handler().Handle(new ReissueInvitationCommandRequest(userId), CancellationToken.None);

    private ReissueInvitationCommandRequestHandler Handler() =>
        new(
            _users,
            _activationTokens,
            _activationTokenFactory,
            _links,
            _emails,
            _clock,
            NullLogger<ReissueInvitationCommandRequestHandler>.Instance);

    private User User()
    {
        var user = new User(
            Guid.NewGuid(),
            OrgId,
            $"user-{Guid.NewGuid():N}@acme.test",
            "hash",
            "A Person",
            ActorId,
            NowUtc.AddDays(-7));
        user.AddRole(Role.Member, ActorId, NowUtc.AddDays(-7));

        _users.Users.Add(user);
        return user;
    }

    private ActivationToken LiveToken(Guid userId)
    {
        var token = new ActivationToken(
            Guid.NewGuid(), userId, $"hash-{Guid.NewGuid():N}", NowUtc.AddDays(-1), NowUtc.AddDays(6));
        _activationTokens.Tokens.Add(token);
        return token;
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

        public CancellationToken? LastReceivedToken { get; private set; }

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            LastReceivedToken = cancellationToken;

            if (NextSendFails)
            {
                return Task.FromResult(EmailSendResult.Failed("the provider refused"));
            }

            Sent.Add(message);
            return Task.FromResult(EmailSendResult.Success());
        }
    }
}
