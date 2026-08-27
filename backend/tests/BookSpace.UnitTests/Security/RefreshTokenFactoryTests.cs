using BookSpace.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace BookSpace.UnitTests.Security;

public class RefreshTokenFactoryTests
{
    [Fact]
    public void Create_ProducesADifferentTokenEveryCall()
    {
        var factory = CreateFactory();

        var first = factory.Create();
        var second = factory.Create();

        Assert.NotEqual(first.RawToken, second.RawToken);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Create_NeverStoresTheRawToken()
    {
        var factory = CreateFactory();

        var generated = factory.Create();

        // The whole point of hashing: what lands in the database must not be
        // usable as the token itself (FR-2.3).
        Assert.NotEqual(generated.RawToken, generated.Hash);
    }

    // The property the unique-index lookup depends on: the same token must hash
    // to the same value every time. A salted KDF would fail this test, which is
    // why refresh tokens deliberately don't use one (decision 0011).
    [Fact]
    public void HashOf_IsDeterministic()
    {
        var factory = CreateFactory();
        var generated = factory.Create();

        Assert.Equal(generated.Hash, factory.HashOf(generated.RawToken));
        Assert.Equal(factory.HashOf(generated.RawToken), factory.HashOf(generated.RawToken));
    }

    [Fact]
    public void HashOf_DifferentTokensHashDifferently()
    {
        var factory = CreateFactory();

        Assert.NotEqual(factory.HashOf("token-a"), factory.HashOf("token-b"));
    }

    [Fact]
    public void Create_RawTokenIsUrlSafe()
    {
        var factory = CreateFactory();

        var raw = factory.Create().RawToken;

        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
        Assert.DoesNotContain('=', raw);
    }

    [Fact]
    public void Lifetime_ComesFromConfiguration()
    {
        var factory = CreateFactory(refreshTokenDays: 30);

        Assert.Equal(TimeSpan.FromDays(30), factory.Lifetime);
    }

    private static RefreshTokenFactory CreateFactory(int refreshTokenDays = 14) =>
        new(Options.Create(new JwtOptions { RefreshTokenDays = refreshTokenDays }));
}
