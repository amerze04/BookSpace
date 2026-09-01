using BookSpace.Application.Features.Authentication.Logout;
using BookSpace.Domain.Entities;
using BookSpace.UnitTests.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookSpace.UnitTests.Authentication;

// FR-2.4 "sessions can be revoked".
public class LogoutCommandRequestHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    // Logging out ends the session, and a family is exactly one session's chain
    // of rotations — so the family goes, not just the token presented.
    [Fact]
    public async Task Handle_KnownToken_RevokesTheWholeFamily()
    {
        var harness = new Harness();
        var familyId = Guid.NewGuid();
        var presented = harness.AddToken(raw: "current", familyId: familyId);
        var sibling = harness.AddToken(raw: "sibling", familyId: familyId);

        await harness.Handle("current");

        Assert.False(presented.IsActive);
        Assert.False(sibling.IsActive);
    }

    [Fact]
    public async Task Handle_KnownToken_LeavesOtherSessionsSignedIn()
    {
        var harness = new Harness();
        harness.AddToken(raw: "current", familyId: Guid.NewGuid());
        var otherDevice = harness.AddToken(raw: "other-device", familyId: Guid.NewGuid());

        await harness.Handle("current");

        Assert.True(otherDevice.IsActive);
    }

    // Succeeds silently rather than reporting "no such token", so logout cannot
    // be used to probe which tokens are live.
    [Fact]
    public async Task Handle_UnknownToken_SucceedsWithoutRevokingAnything()
    {
        var harness = new Harness();
        var unrelated = harness.AddToken(raw: "someone-elses", familyId: Guid.NewGuid());

        await harness.Handle("never-issued");

        Assert.True(unrelated.IsActive);
        Assert.Equal(0, harness.RefreshTokens.SaveCount);
    }

    private sealed class Harness
    {
        public FakeRefreshTokenRepository RefreshTokens { get; } = new();
        public FakeRefreshTokenFactory TokenFactory { get; } = new();
        private readonly TestClock _clock = new(Now);

        public RefreshToken AddToken(string raw, Guid familyId)
        {
            var token = new RefreshToken(
                Guid.NewGuid(),
                Guid.NewGuid(),
                TokenFactory.HashOf(raw),
                familyId,
                issuedAtUtc: Now.AddMinutes(-10),
                expiresAtUtc: Now.AddDays(14));

            RefreshTokens.Add(token);
            return token;
        }

        public Task Handle(string rawToken)
        {
            var handler = new LogoutCommandRequestHandler(
                RefreshTokens,
                TokenFactory,
                _clock,
                NullLogger<LogoutCommandRequestHandler>.Instance);

            return handler.Handle(new LogoutCommandRequest(rawToken), CancellationToken.None);
        }
    }
}
