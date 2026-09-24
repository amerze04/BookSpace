using System.ComponentModel.DataAnnotations;
using BookSpace.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace BookSpace.UnitTests.Security;

// The link is the entire invitation — if it is malformed, the account it was
// created for can never be used, and there is no way to re-issue one
// (docs/user-management-plan.md §6). Worth more tests than its size suggests.
public class ActivationLinkBuilderTests
{
    private const string RawToken = "dGhpcy1pcy1hLWJhc2U2NHVybC10b2tlbg";

    [Fact]
    public void ItAppendsTheTokenToTheConfiguredPage()
    {
        var link = Build("https://bookspace.test/activate");

        Assert.Equal($"https://bookspace.test/activate?token={RawToken}", link);
    }

    // The case the concatenation this replaces would have got wrong: a
    // configured URL that already carries a query string would have produced a
    // second '?' and a link that resolves to the wrong page — invisible in the
    // default configuration, which has none.
    [Fact]
    public void ItPreservesAQueryStringTheConfiguredPageAlreadyHas()
    {
        var link = Build("https://bookspace.test/activate?lang=en");

        Assert.Equal($"https://bookspace.test/activate?lang=en&token={RawToken}", link);
    }

    [Fact]
    public void ItKeepsThePathAndPort()
    {
        var link = Build("http://localhost:4200/activate");

        Assert.StartsWith("http://localhost:4200/activate?", link);
    }

    // SecureToken produces Base64Url precisely so nothing here needs escaping,
    // and this is the test that keeps that true from the other end: if the
    // token alphabet ever changes, the link still has to survive being a URL.
    [Fact]
    public void ItEscapesATokenThatWouldOtherwiseNeedIt()
    {
        var link = Build("https://bookspace.test/activate", "a+b/c=d&e");

        Assert.Contains("token=a%2Bb%2Fc%3Dd%26e", link, StringComparison.Ordinal);
        Assert.DoesNotContain("&e", link, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProducedLinkIsAWellFormedAbsoluteUri()
    {
        var link = Build("https://bookspace.test/activate");

        Assert.True(Uri.TryCreate(link, UriKind.Absolute, out var uri));
        Assert.Equal("https", uri!.Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankTokenIsRefused(string rawToken)
    {
        Assert.Throws<ArgumentException>(() => Build("https://bookspace.test/activate", rawToken));
    }

    // ValidateDataAnnotations + ValidateOnStart act on these, so a deployment
    // with no activation page fails the boot rather than emailing links that
    // lead nowhere.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    public void AMissingOrMalformedActivationUrl_IsInvalidConfiguration(string activationUrl)
    {
        var options = new ActivationOptions { ActivationUrl = activationUrl };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ActivationOptions.ActivationUrl)));
    }

    [Fact]
    public void AConfiguredActivationUrl_IsValidConfiguration()
    {
        var options = new ActivationOptions { ActivationUrl = "http://localhost:4200/activate" };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            options, new ValidationContext(options), results, validateAllProperties: true);

        Assert.Empty(results);
    }

    // No default, deliberately — the API cannot guess its frontend's origin,
    // and a guess would produce invitations that look right and lead nowhere.
    [Fact]
    public void ThereIsNoDefaultActivationUrl()
    {
        Assert.Equal(string.Empty, new ActivationOptions().ActivationUrl);
    }

    private static string Build(string activationUrl, string rawToken = RawToken) =>
        new ActivationLinkBuilder(Options.Create(new ActivationOptions { ActivationUrl = activationUrl }))
            .BuildFor(rawToken);
}
