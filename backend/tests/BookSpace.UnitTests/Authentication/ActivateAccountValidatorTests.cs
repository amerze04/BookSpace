using BookSpace.Application.Features.Authentication;
using BookSpace.Application.Features.Authentication.Activate;

namespace BookSpace.UnitTests.Authentication;

// The password policy is enforced here, and it is the first one this
// application has ever had (see PasswordPolicy for why it had to be invented
// and on what basis). Pinning it means a later change to the numbers is a
// deliberate edit rather than something that drifts.
public class ActivateAccountValidatorTests
{
    private readonly ActivateAccountCommandRequestValidator _validator = new();

    [Fact]
    public void AWellFormedRequest_IsValid()
    {
        Assert.True(_validator.Validate(Request()).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingToken_IsInvalid(string token)
    {
        Assert.False(_validator.Validate(Request(token: token)).IsValid);
    }

    // Comfortably above the 43 characters a 256-bit Base64Url token takes, and
    // low enough that nothing enormous reaches the hash function on an
    // anonymous endpoint.
    [Fact]
    public void AnAbsurdlyLongToken_IsInvalid()
    {
        Assert.False(_validator.Validate(Request(token: new string('t', 513))).IsValid);
    }

    [Fact]
    public void ARealLengthToken_IsValid()
    {
        Assert.True(_validator.Validate(Request(token: new string('t', 43))).IsValid);
    }

    [Fact]
    public void APasswordShorterThanThePolicy_IsInvalid()
    {
        var password = new string('p', PasswordPolicy.MinimumLength - 1);

        Assert.False(_validator.Validate(Request(password: password)).IsValid);
    }

    [Fact]
    public void APasswordExactlyAtTheMinimum_IsValid()
    {
        var password = new string('p', PasswordPolicy.MinimumLength);

        Assert.True(_validator.Validate(Request(password: password)).IsValid);
    }

    // Not cosmetic: IPasswordHasher runs PBKDF2 at 100k iterations over
    // whatever it is given, so an unbounded password is a cheap way to make an
    // anonymous endpoint do unbounded work.
    [Fact]
    public void APasswordBeyondTheMaximum_IsInvalid()
    {
        var password = new string('p', PasswordPolicy.MaximumLength + 1);

        Assert.False(_validator.Validate(Request(password: password)).IsValid);
    }

    [Fact]
    public void APasswordExactlyAtTheMaximum_IsValid()
    {
        var password = new string('p', PasswordPolicy.MaximumLength);

        Assert.True(_validator.Validate(Request(password: password)).IsValid);
    }

    // No composition rules, following NIST SP 800-63B — length is what helps,
    // and "must contain a digit and a symbol" pushes people towards Password1!.
    // Asserted so that adding one later is a visible decision.
    [Theory]
    [InlineData("all lower case letters")]
    [InlineData("ALL UPPER CASE LETTERS")]
    [InlineData("123456789012345678")]
    [InlineData("correct horse battery staple")]
    public void ALongPasswordWithNoCharacterVariety_IsValid(string password)
    {
        Assert.True(_validator.Validate(Request(password: password)).IsValid);
    }

    [Fact]
    public void ThePolicyIsTheDocumentedOne()
    {
        Assert.Equal(12, PasswordPolicy.MinimumLength);
        Assert.Equal(128, PasswordPolicy.MaximumLength);
    }

    private static ActivateAccountCommandRequest Request(
        string token = "an-activation-token",
        string password = "a-long-enough-password") => new(token, password);
}
