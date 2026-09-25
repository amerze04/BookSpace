using System.ComponentModel.DataAnnotations;
using BookSpace.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace BookSpace.UnitTests.Security;

// Mirrors RefreshTokenFactoryTests deliberately. The two factories share
// SecureToken, so most of what is asserted here is the same property proved
// twice — which is the point: if somebody changes the shared primitive to suit
// one caller, both suites say so rather than one.
public class ActivationTokenFactoryTests
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

    // FR-2.3's shape applied to invitations: what lands in the database must
    // not be usable as the token itself. The plaintext exists only in the email.
    [Fact]
    public void Create_NeverStoresTheRawToken()
    {
        var generated = CreateFactory().Create();

        Assert.NotEqual(generated.RawToken, generated.Hash);
    }

    // The property the unique-index lookup depends on. A salted KDF would fail
    // this, which is why neither token type uses one (decision 0011).
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

    // Load-bearing here in a way it is not for a refresh token: this value goes
    // into a link in an email, so a '+' or '/' would be mangled by the first
    // client that percent-encodes it.
    [Fact]
    public void Create_RawTokenIsUrlSafe()
    {
        var raw = CreateFactory().Create().RawToken;

        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
        Assert.DoesNotContain('=', raw);
    }

    // Same primitive, so the same entropy — 32 bytes Base64Url-encoded is 43
    // characters. Worth pinning: "shorten the token so the link is prettier" is
    // a plausible future change and a bad one.
    [Fact]
    public void Create_RawTokenCarries256BitsOfEntropy()
    {
        Assert.Equal(43, CreateFactory().Create().RawToken.Length);
    }

    // The two factories must agree, because they share SecureToken. If they
    // ever stopped, the symptom would be an activation link that nothing can
    // find in the table.
    [Fact]
    public void HashOf_MatchesTheRefreshTokenFactorysHashOfTheSameValue()
    {
        var activation = CreateFactory();
        var refresh = new RefreshTokenFactory(Options.Create(new JwtOptions()));

        Assert.Equal(refresh.HashOf("a-shared-value"), activation.HashOf("a-shared-value"));
    }

    [Fact]
    public void Lifetime_ComesFromConfiguration()
    {
        Assert.Equal(TimeSpan.FromDays(30), CreateFactory(tokenLifetimeDays: 30).Lifetime);
    }

    [Fact]
    public void TheDefaultLifetime_IsTheDocumentedWeek()
    {
        Assert.Equal(7, new ActivationOptions().TokenLifetimeDays);
        Assert.Equal(TimeSpan.FromDays(7), CreateFactory().Lifetime);
    }

    // ValidateDataAnnotations + ValidateOnStart act on these, so a lifetime of
    // zero days — which would issue tokens that are expired on arrival — fails
    // the boot rather than the first invitation.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(91)]
    public void ALifetimeOutsideTheAllowedRange_IsInvalid(int days)
    {
        var options = new ActivationOptions { TokenLifetimeDays = days };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ActivationOptions.TokenLifetimeDays)));
    }

    private static ActivationTokenFactory CreateFactory(int tokenLifetimeDays = 7) =>
        new(Options.Create(new ActivationOptions { TokenLifetimeDays = tokenLifetimeDays }));
}
