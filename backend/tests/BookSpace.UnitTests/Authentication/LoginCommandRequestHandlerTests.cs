using BookSpace.Application.Features.Authentication;
using BookSpace.Application.Features.Authentication.Login;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookSpace.UnitTests.Authentication;

// FR-2.1 / FR-2.3 / FR-2.4.
public class LoginCommandRequestHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Handle_ValidCredentials_ReturnsAccessAndRefreshTokens()
    {
        var harness = new Harness();
        harness.AddUser("member@acme.test");

        var result = await harness.Handle("member@acme.test", "Passw0rd!");

        Assert.NotEmpty(result.AccessToken);
        Assert.NotEmpty(result.RefreshToken);
        Assert.Equal(900, result.ExpiresIn); // 15 minutes, in seconds
    }

    // FR-2.3: what lands in the repository is the hash, never the token the
    // client was handed.
    [Fact]
    public async Task Handle_ValidCredentials_PersistsOnlyTheHashedRefreshToken()
    {
        var harness = new Harness();
        harness.AddUser("member@acme.test");

        var result = await harness.Handle("member@acme.test", "Passw0rd!");

        var stored = Assert.Single(harness.RefreshTokens.Tokens);
        Assert.NotEqual(result.RefreshToken, stored.TokenHash);
        Assert.Equal(harness.TokenFactory.HashOf(result.RefreshToken), stored.TokenHash);
        Assert.Equal(1, harness.RefreshTokens.SaveCount);
    }

    [Fact]
    public async Task Handle_ValidCredentials_StartsANewFamilyExpiringAtTheConfiguredLifetime()
    {
        var harness = new Harness();
        harness.AddUser("member@acme.test");

        await harness.Handle("member@acme.test", "Passw0rd!");

        var stored = Assert.Single(harness.RefreshTokens.Tokens);
        Assert.NotEqual(Guid.Empty, stored.FamilyId);
        Assert.Equal(Now.AddDays(14), stored.ExpiresAtUtc);
        Assert.True(stored.IsActive);
    }

    // Email is normalized on both sides, so case and stray whitespace don't
    // decide whether someone can log in (decision 0010).
    [Theory]
    [InlineData("MEMBER@ACME.TEST")]
    [InlineData("  member@acme.test  ")]
    public async Task Handle_EmailDifferingInCaseOrWhitespace_StillAuthenticates(string submitted)
    {
        var harness = new Harness();
        harness.AddUser("member@acme.test");

        var result = await harness.Handle(submitted, "Passw0rd!");

        Assert.NotEmpty(result.AccessToken);
    }

    // The four failure paths below must be indistinguishable to the caller, so
    // login can't be used to enumerate accounts or probe account state.
    [Fact]
    public async Task Handle_UnknownEmail_ThrowsInvalidCredentials()
    {
        var harness = new Harness();

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("nobody@acme.test", "Passw0rd!"));

        Assert.Equal(AuthenticationFailureReason.InvalidCredentials, exception.ReasonCode);
    }

    [Fact]
    public async Task Handle_WrongPassword_ThrowsInvalidCredentials()
    {
        var harness = new Harness();
        harness.AddUser("member@acme.test");

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("member@acme.test", "wrong-password"));

        Assert.Equal(AuthenticationFailureReason.InvalidCredentials, exception.ReasonCode);
    }

    [Fact]
    public async Task Handle_DeactivatedUser_ThrowsInvalidCredentials()
    {
        var harness = new Harness();
        var user = harness.AddUser("member@acme.test");
        user.Deactivate(user.Id, Now);

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("member@acme.test", "Passw0rd!"));

        Assert.Equal(AuthenticationFailureReason.InvalidCredentials, exception.ReasonCode);
    }

    [Fact]
    public async Task Handle_SuspendedOrganization_ThrowsInvalidCredentials()
    {
        var harness = new Harness();
        harness.AddUser("member@acme.test", OrganizationStatus.Suspended);

        var exception = await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("member@acme.test", "Passw0rd!"));

        Assert.Equal(AuthenticationFailureReason.InvalidCredentials, exception.ReasonCode);
    }

    [Fact]
    public async Task Handle_FailedLogin_PersistsNothing()
    {
        var harness = new Harness();
        harness.AddUser("member@acme.test");

        await Assert.ThrowsAsync<AuthenticationException>(
            () => harness.Handle("member@acme.test", "wrong-password"));

        Assert.Empty(harness.RefreshTokens.Tokens);
        Assert.Equal(0, harness.RefreshTokens.SaveCount);
    }

    // A SysAdmin has no organization, so the org-status check must not reject them.
    [Fact]
    public async Task Handle_SysAdminWithNoOrganization_Authenticates()
    {
        var harness = new Harness();
        harness.AddSysAdmin("sysadmin@bookspace.local");

        var result = await harness.Handle("sysadmin@bookspace.local", "Passw0rd!");

        Assert.NotEmpty(result.AccessToken);
    }

    private sealed class Harness
    {
        public FakeAuthenticationUserRepository Users { get; } = new();
        public FakeRefreshTokenRepository RefreshTokens { get; } = new();
        public FakeRefreshTokenFactory TokenFactory { get; } = new();
        private readonly RecordingPasswordHasher _passwordHasher = new();
        private readonly TestClock _clock = new(Now);

        public User AddUser(
            string email,
            OrganizationStatus? organizationStatus = OrganizationStatus.Active,
            Role role = Role.Member)
        {
            var id = Guid.NewGuid();
            var user = new User(id, Guid.NewGuid(), email, "stored-hash", "Test User", id, Now);
            user.AddRole(role, id, Now);
            Users.Add(user, organizationStatus);
            return user;
        }

        // OrgId null and no organization status — the SysAdmin shape.
        public User AddSysAdmin(string email)
        {
            var id = Guid.NewGuid();
            var user = new User(id, orgId: null, email, "stored-hash", "System Administrator", id, Now);
            user.AddRole(Role.SysAdmin, id, Now);
            Users.Add(user, organizationStatus: null);
            return user;
        }

        public Task<LoginCommandResponse> Handle(string email, string password)
        {
            var issuer = new TokenIssuer(new FakeAccessTokenService(), TokenFactory, RefreshTokens, _clock);
            var handler = new LoginCommandRequestHandler(
                Users,
                RefreshTokens,
                _passwordHasher,
                issuer,
                NullLogger<LoginCommandRequestHandler>.Instance);

            return handler.Handle(new LoginCommandRequest(email, password), CancellationToken.None);
        }
    }
}
