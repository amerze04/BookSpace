using BookSpace.Application.Features.Authentication;
using BookSpace.Application.Features.Authentication.Refresh;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookSpace.UnitTests.Authentication;

// FR-2.2 in full — one test per row of the decision table in
// RefreshTokenCommandHandler, because the difference between the rows (revoke
// one token vs. revoke the whole family) is the entire security property.
public class RefreshTokenCommandHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Handle_ActiveToken_RotatesAndReturnsANewPair()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var original = harness.AddToken(user.Id, raw: "original");

        var result = await harness.Handle("original");

        Assert.NotEmpty(result.AccessToken);
        Assert.NotEqual("original", result.RefreshToken);
        Assert.False(original.IsActive);
    }

    [Fact]
    public async Task Handle_ActiveToken_PointsTheOldTokenAtItsReplacement()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var original = harness.AddToken(user.Id, raw: "original");

        var result = await harness.Handle("original");

        var replacement = harness.Replacement(result);

        Assert.Equal(replacement.Id, original.ReplacedByTokenId);
        Assert.Equal(Now, original.RevokedAtUtc);
    }

    [Fact]
    public async Task Handle_ActiveToken_ReplacementStaysInTheSameFamily()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var original = harness.AddToken(user.Id, raw: "original");

        var result = await harness.Handle("original");

        Assert.Equal(original.FamilyId, harness.Replacement(result).FamilyId);
    }

    // The window is absolute: rotating inherits the original expiry rather than
    // restarting the 14 days, so a stolen token cannot be renewed indefinitely.
    [Fact]
    public async Task Handle_ActiveToken_ReplacementInheritsTheOriginalExpiry()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var original = harness.AddToken(user.Id, raw: "original", expiresAtUtc: Now.AddDays(3));

        var result = await harness.Handle("original");

        Assert.Equal(original.ExpiresAtUtc, harness.Replacement(result).ExpiresAtUtc);
    }

    [Fact]
    public async Task Handle_UnknownToken_ThrowsInvalidRefreshTokenAndRevokesNothing()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var unrelated = harness.AddToken(user.Id, raw: "legitimate");

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("never-issued"));

        Assert.Equal(AuthenticationFailureReason.InvalidRefreshToken, exception.ReasonCode);
        Assert.True(unrelated.IsActive);
    }

    // The core of FR-2.2: presenting a token that was already rotated means
    // someone holds a copy they should not have, so the whole family dies.
    [Fact]
    public async Task Handle_AlreadyRevokedToken_RevokesTheEntireFamily()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var familyId = Guid.NewGuid();
        var stolen = harness.AddToken(user.Id, raw: "stolen", familyId: familyId);
        var current = harness.AddToken(user.Id, raw: "current", familyId: familyId);
        stolen.Revoke(Now.AddMinutes(-5), current.Id);

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("stolen"));

        Assert.Equal(AuthenticationFailureReason.RefreshTokenReuseDetected, exception.ReasonCode);
        Assert.False(current.IsActive);
    }

    [Fact]
    public async Task Handle_ReuseDetected_LeavesOtherFamiliesAlone()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var stolen = harness.AddToken(user.Id, raw: "stolen", familyId: Guid.NewGuid());
        stolen.Revoke(Now.AddMinutes(-5));

        // A second, unrelated session for the same user — another device.
        var otherSession = harness.AddToken(user.Id, raw: "other-device", familyId: Guid.NewGuid());

        await Assert.ThrowsAsync<AuthenticationException>(() => harness.Handle("stolen"));

        Assert.True(otherSession.IsActive);
    }

    // Expiry is ordinary lifecycle, not evidence of theft — so it costs the one
    // token, not the family.
    [Fact]
    public async Task Handle_ExpiredToken_RevokesOnlyThatTokenAndNotTheFamily()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var familyId = Guid.NewGuid();
        var expired = harness.AddToken(user.Id, raw: "expired", familyId: familyId, expiresAtUtc: Now.AddSeconds(-1));
        var sibling = harness.AddToken(user.Id, raw: "sibling", familyId: familyId);

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("expired"));

        Assert.Equal(AuthenticationFailureReason.RefreshTokenExpired, exception.ReasonCode);
        Assert.False(expired.IsActive);
        Assert.True(sibling.IsActive);
    }

    // FR-2.4
    [Fact]
    public async Task Handle_UserDeactivatedSinceLogin_RevokesTheFamily()
    {
        var harness = new Harness();
        var user = harness.AddUser();
        var token = harness.AddToken(user.Id, raw: "current");
        user.Deactivate(user.Id, Now);

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("current"));

        Assert.Equal(AuthenticationFailureReason.AccountInactive, exception.ReasonCode);
        Assert.False(token.IsActive);
    }

    [Fact]
    public async Task Handle_OrganizationSuspendedSinceLogin_RevokesTheFamily()
    {
        var harness = new Harness();
        var user = harness.AddUser(OrganizationStatus.Suspended);
        var token = harness.AddToken(user.Id, raw: "current");

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("current"));

        Assert.Equal(AuthenticationFailureReason.AccountInactive, exception.ReasonCode);
        Assert.False(token.IsActive);
    }

    private sealed class Harness
    {
        public FakeAuthenticationUserRepository Users { get; } = new();
        public FakeRefreshTokenRepository RefreshTokens { get; } = new();
        public FakeRefreshTokenFactory TokenFactory { get; } = new();
        private readonly TestClock _clock = new(Now);

        public User AddUser(OrganizationStatus? organizationStatus = OrganizationStatus.Active)
        {
            var id = Guid.NewGuid();
            var user = new User(id, Guid.NewGuid(), "member@acme.test", "stored-hash", "Test User", id, Now);
            user.AddRole(Role.Member, id, Now);
            Users.Add(user, organizationStatus);
            return user;
        }

        public RefreshToken AddToken(
            Guid userId,
            string raw,
            Guid? familyId = null,
            DateTime? expiresAtUtc = null)
        {
            var token = new RefreshToken(
                Guid.NewGuid(),
                userId,
                TokenFactory.HashOf(raw),
                familyId ?? Guid.NewGuid(),
                issuedAtUtc: Now.AddMinutes(-10),
                expiresAtUtc: expiresAtUtc ?? Now.AddDays(14));

            RefreshTokens.Add(token);
            return token;
        }

        public RefreshToken Replacement(AuthenticationResult result) =>
            RefreshTokens.Tokens.Single(t => t.TokenHash == TokenFactory.HashOf(result.RefreshToken));

        public Task<AuthenticationResult> Handle(string rawToken)
        {
            var issuer = new TokenIssuer(new FakeAccessTokenService(), TokenFactory, RefreshTokens, _clock);
            var handler = new RefreshTokenCommandHandler(
                Users,
                RefreshTokens,
                TokenFactory,
                issuer,
                _clock,
                NullLogger<RefreshTokenCommandHandler>.Instance);

            return handler.Handle(new RefreshTokenCommand(rawToken), CancellationToken.None);
        }
    }
}
