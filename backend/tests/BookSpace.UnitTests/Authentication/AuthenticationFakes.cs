using BookSpace.Application.Abstractions;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;

namespace BookSpace.UnitTests.Authentication;

// In-memory stand-ins for the two auth repositories. The handler logic under
// test — the FR-2.2 decision table — is pure application logic, so it's worth
// exercising here where every branch is cheap to set up. The same behavior is
// re-proved against real SQL Server in BookSpace.IntegrationTests, which is
// where the constraint and locking behavior actually lives.
internal sealed class FakeAuthenticationUserRepository : IAuthenticationUserRepository
{
    private readonly List<AuthenticatedUser> _users = new();

    public void Add(User user, OrganizationStatus? organizationStatus = OrganizationStatus.Active) =>
        _users.Add(new AuthenticatedUser(user, organizationStatus));

    public Task<AuthenticatedUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(_users.FirstOrDefault(u => u.User.Email == normalizedEmail));

    public Task<AuthenticatedUser?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_users.FirstOrDefault(u => u.User.Id == userId));
}

internal sealed class FakeRefreshTokenRepository : IRefreshTokenRepository
{
    public List<RefreshToken> Tokens { get; } = new();

    public int SaveCount { get; private set; }

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(Tokens.FirstOrDefault(t => t.TokenHash == tokenHash));

    public void Add(RefreshToken token) => Tokens.Add(token);

    public Task RevokeFamilyAsync(Guid familyId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        foreach (var token in Tokens.Where(t => t.FamilyId == familyId && t.IsActive))
        {
            token.Revoke(nowUtc);
        }

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

// Predictable and reversible, so a test can compute the hash of a token it holds
// without depending on SHA-256. Determinism is what the real factory guarantees
// and what these handlers rely on.
internal sealed class FakeRefreshTokenFactory : IRefreshTokenFactory
{
    private int _next;

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(14);

    public GeneratedRefreshToken Create()
    {
        var raw = $"raw-token-{++_next}";
        return new GeneratedRefreshToken(raw, HashOf(raw));
    }

    public string HashOf(string rawToken) => $"hash::{rawToken}";
}

internal sealed class FakeAccessTokenService : IAccessTokenService
{
    public DateTime ExpiresAtUtc { get; set; } = new(2026, 8, 26, 12, 15, 0, DateTimeKind.Utc);

    public AccessToken Issue(User user) => new($"access-token-for-{user.Id}", ExpiresAtUtc);
}

internal sealed class RecordingPasswordHasher : IPasswordHasher
{
    // The password that Verify accepts; anything else fails.
    public string CorrectPassword { get; set; } = "Passw0rd!";

    public string Hash(string password) => $"hash::{password}";

    public bool Verify(string passwordHash, string password) => password == CorrectPassword;
}
