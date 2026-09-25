using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests;

// The domain half of account activation. Small, but every rule here is one the
// endpoint's security argument rests on, so none of it is left to the handler.
public class ActivationTokenTests
{
    private static readonly DateTime Issued = new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AFreshToken_IsRedeemable()
    {
        var token = Token();

        Assert.False(token.IsConsumed);
        Assert.True(token.CanBeRedeemed(Issued.AddDays(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ATokenWithoutAHash_IsRejected(string hash)
    {
        Assert.Throws<ArgumentException>(() =>
            new ActivationToken(Guid.NewGuid(), Guid.NewGuid(), hash, Issued, Issued.AddDays(7)));
    }

    // A token that expires when or before it is issued is redeemable for no
    // time at all, which is never what the caller meant.
    [Fact]
    public void ATokenExpiringNoLaterThanItWasIssued_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new ActivationToken(Guid.NewGuid(), Guid.NewGuid(), "hash", Issued, Issued));

        Assert.Throws<ArgumentException>(() =>
            new ActivationToken(Guid.NewGuid(), Guid.NewGuid(), "hash", Issued, Issued.AddSeconds(-1)));
    }

    // The boundary is inclusive, matching RefreshToken's own `ExpiresAtUtc <=
    // now` check — so the two agree about what expired means rather than
    // differing by a second nobody would ever find.
    [Fact]
    public void ExpiryIsInclusiveOfItsOwnInstant()
    {
        var token = Token();
        var expiresAt = Issued.AddDays(7);

        Assert.False(token.HasExpired(expiresAt.AddSeconds(-1)));
        Assert.True(token.HasExpired(expiresAt));
        Assert.True(token.HasExpired(expiresAt.AddSeconds(1)));
    }

    [Fact]
    public void AnExpiredToken_CannotBeRedeemed()
    {
        var token = Token();

        Assert.False(token.CanBeRedeemed(Issued.AddDays(8)));
    }

    [Fact]
    public void Consuming_RecordsWhenAndBlocksReuse()
    {
        var token = Token();
        var consumedAt = Issued.AddHours(2);

        token.Consume(consumedAt);

        Assert.True(token.IsConsumed);
        Assert.Equal(consumedAt, token.ConsumedAtUtc);
        Assert.False(token.CanBeRedeemed(consumedAt));
    }

    // Throws rather than refusing quietly: the handler checks CanBeRedeemed
    // first and reports the generic failure, so reaching this means the caller
    // skipped that check — a bug, not a request to turn down politely.
    [Fact]
    public void ConsumingTwice_Throws()
    {
        var token = Token();
        token.Consume(Issued.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => token.Consume(Issued.AddHours(2)));
    }

    // The row survives being spent (CLAUDE.md §4.5) — and keeping it is the only
    // reason a second attempt is distinguishable from a first at all.
    [Fact]
    public void AConsumedToken_KeepsItsOriginalDetails()
    {
        var userId = Guid.NewGuid();
        var token = new ActivationToken(Guid.NewGuid(), userId, "hash", Issued, Issued.AddDays(7));

        token.Consume(Issued.AddHours(1));

        Assert.Equal(userId, token.UserId);
        Assert.Equal("hash", token.TokenHash);
        Assert.Equal(Issued, token.IssuedAtUtc);
    }

    private static ActivationToken Token() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "token-hash", Issued, Issued.AddDays(7));
}
