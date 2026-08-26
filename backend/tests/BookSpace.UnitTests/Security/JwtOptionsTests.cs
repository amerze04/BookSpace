using System.ComponentModel.DataAnnotations;
using BookSpace.Infrastructure.Security;

namespace BookSpace.UnitTests.Security;

// These annotations are what ValidateDataAnnotations().ValidateOnStart() acts on,
// so they're the whole of the "app refuses to boot without a usable signing key"
// guarantee (CLAUDE.md §4.4, decision 0009). A dropped [Required] would silently
// re-allow booting with an empty key, so the attributes are worth pinning.
public class JwtOptionsTests
{
    [Fact]
    public void FullyConfiguredOptions_AreValid()
    {
        Assert.Empty(Validate(Valid()));
    }

    [Fact]
    public void MissingSigningKey_IsInvalid()
    {
        var options = Valid();
        options.SigningKey = string.Empty;

        Assert.Contains(Validate(options), r => r.MemberNames.Contains(nameof(JwtOptions.SigningKey)));
    }

    // 32 characters = 256 bits, matching HMAC-SHA256. A shorter key must be
    // rejected rather than silently accepted and zero-padded by the primitive.
    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    public void SigningKeyShorterThan32Characters_IsInvalid(int length)
    {
        var options = Valid();
        options.SigningKey = new string('k', length);

        Assert.Contains(Validate(options), r => r.MemberNames.Contains(nameof(JwtOptions.SigningKey)));
    }

    [Fact]
    public void SigningKeyOfExactly32Characters_IsValid()
    {
        var options = Valid();
        options.SigningKey = new string('k', 32);

        Assert.Empty(Validate(options));
    }

    [Fact]
    public void MissingIssuerOrAudience_IsInvalid()
    {
        var noIssuer = Valid();
        noIssuer.Issuer = string.Empty;

        var noAudience = Valid();
        noAudience.Audience = string.Empty;

        Assert.NotEmpty(Validate(noIssuer));
        Assert.NotEmpty(Validate(noAudience));
    }

    [Fact]
    public void DefaultLifetimes_MatchTheDocumentedDecision()
    {
        var options = new JwtOptions();

        // decision 0009: 15-minute access token, 14-day refresh window.
        Assert.Equal(15, options.AccessTokenMinutes);
        Assert.Equal(14, options.RefreshTokenDays);
    }

    [Fact]
    public void NonPositiveLifetimes_AreInvalid()
    {
        var options = Valid();
        options.AccessTokenMinutes = 0;

        Assert.Contains(Validate(options), r => r.MemberNames.Contains(nameof(JwtOptions.AccessTokenMinutes)));
    }

    private static JwtOptions Valid() => new()
    {
        Issuer = "BookSpace.Api",
        Audience = "BookSpace.Client",
        SigningKey = new string('k', 40),
    };

    private static List<ValidationResult> Validate(JwtOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
