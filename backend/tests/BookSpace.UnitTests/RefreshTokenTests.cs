using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

public class RefreshTokenTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid FamilyId = Guid.NewGuid();
    private static readonly DateTime IssuedAt = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpiresAt = IssuedAt.AddDays(30);

    private static RefreshToken CreateValid() =>
        new(Guid.NewGuid(), UserId, "hashed-token-value", FamilyId, IssuedAt, ExpiresAt);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnBlankTokenHash(string tokenHash)
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.NewGuid(), UserId, tokenHash, FamilyId, IssuedAt, ExpiresAt));
    }

    [Fact]
    public void Constructor_Throws_WhenExpiresAtUtcIsNotAfterIssuedAtUtc()
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.NewGuid(), UserId, "hash", FamilyId, IssuedAt, IssuedAt));
    }

    [Fact]
    public void IsActive_IsTrue_UntilRevoked()
    {
        var token = CreateValid();

        Assert.True(token.IsActive);
    }

    [Fact]
    public void Revoke_SetsRevokedAtUtcAndIsActiveFalse()
    {
        var token = CreateValid();
        var revokedAt = IssuedAt.AddDays(1);

        token.Revoke(revokedAt);

        Assert.False(token.IsActive);
        Assert.Equal(revokedAt, token.RevokedAtUtc);
        Assert.Null(token.ReplacedByTokenId);
    }

    [Fact]
    public void Revoke_RecordsReplacementTokenId_WhenRotated()
    {
        var token = CreateValid();
        var replacementId = Guid.NewGuid();

        token.Revoke(IssuedAt.AddDays(1), replacementId);

        Assert.Equal(replacementId, token.ReplacedByTokenId);
    }
}
