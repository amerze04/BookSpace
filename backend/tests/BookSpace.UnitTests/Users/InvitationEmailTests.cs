using BookSpace.Application.Features.Users.CreateUser;

namespace BookSpace.UnitTests.Users;

// The only message this application composes, and the only one a recipient
// judges before they trust the product. A pure function, so it is worth
// asserting on directly rather than through the handler.
public class InvitationEmailTests
{
    private static readonly DateTime ExpiresAtUtc = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private const string Link = "https://bookspace.test/activate?token=abc123";

    [Fact]
    public void ItIsAddressedToTheRecipient()
    {
        var message = Compose();

        Assert.Equal("ada@acme.test", message.To.Address);
        Assert.Equal("Ada Lovelace", message.To.DisplayName);
    }

    [Fact]
    public void ItHasASubjectThatSaysWhatItIs()
    {
        Assert.Equal("You have been invited to BookSpace", Compose().Subject);
    }

    // The plain-text half is required, not optional. A recipient whose client
    // refuses HTML still has to be able to get in, and this is the one message
    // where failing to reach them means they never do.
    [Fact]
    public void ThePlainTextBodyCarriesTheLink()
    {
        Assert.Contains(Link, Compose().TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHtmlBodyCarriesTheLinkBothAsAnAnchorAndAsText()
    {
        var html = Compose().HtmlBody;

        Assert.NotNull(html);
        Assert.Contains($"href=\"{Link}\"", html, StringComparison.Ordinal);
        // Also spelled out, for a client that strips anchors or a recipient
        // reading it on a device that will not open one — so the link appears
        // twice, once as an href and once as copyable text.
        var occurrences = html!.Split(Link).Length - 1;
        Assert.Equal(2, occurrences);
    }

    // The link expires and nothing re-issues it, so a recipient who leaves it
    // for a fortnight needs to have been told.
    [Fact]
    public void BothBodiesSayWhenTheLinkStopsWorking()
    {
        var message = Compose();
        var expiry = ExpiresAtUtc.ToString("u");

        Assert.Contains(expiry, message.TextBody, StringComparison.Ordinal);
        Assert.Contains(expiry, message.HtmlBody!, StringComparison.Ordinal);
    }

    // ISO 8601 with a trailing Z. Rendering a local time is impossible here —
    // nothing knows the recipient's zone, and a resource's (decision `0003`) is
    // the wrong answer for a person — so the instant has to be unambiguous.
    [Fact]
    public void TheExpiryIsWrittenAsAnUnambiguousInstant()
    {
        Assert.Contains("2026-10-01 09:00:00Z", Compose().TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBodiesSayWhatToDoIfItWasUnexpected()
    {
        var message = Compose();

        Assert.Contains("were not expecting", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("were not expecting", message.HtmlBody!, StringComparison.Ordinal);
    }

    // The full name comes from an administrator typing into a form, so it is
    // untrusted input going into markup. Unescaped it is at best a broken
    // message and at worst a link the recipient did not expect.
    [Fact]
    public void AFullNameContainingMarkupIsEscapedInTheHtmlBody()
    {
        var message = Compose(fullName: "<script>alert('x')</script> & Co \"Ltd\"");

        Assert.DoesNotContain("<script>", message.HtmlBody!, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", message.HtmlBody!, StringComparison.Ordinal);
        Assert.Contains("&amp; Co", message.HtmlBody!, StringComparison.Ordinal);
        Assert.Contains("&quot;Ltd&quot;", message.HtmlBody!, StringComparison.Ordinal);
        Assert.Contains("&#39;", message.HtmlBody!, StringComparison.Ordinal);
    }

    // The ampersand has to be escaped *first*, or escaping the others
    // double-escapes their own ampersands into &amp;lt; and the message shows
    // the entity rather than the character.
    [Fact]
    public void EscapingDoesNotDoubleEscape()
    {
        var message = Compose(fullName: "<Ada>");

        Assert.Contains("&lt;Ada&gt;", message.HtmlBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;lt;", message.HtmlBody!, StringComparison.Ordinal);
    }

    // The plain-text half is not markup, so escaping it would show the
    // recipient literal entities in their own name.
    [Fact]
    public void ThePlainTextBodyIsNotHtmlEscaped()
    {
        var message = Compose(fullName: "Ada & Co");

        Assert.Contains("Ada & Co", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;", message.TextBody, StringComparison.Ordinal);
    }

    private static Application.Abstractions.EmailMessage Compose(
        string email = "ada@acme.test",
        string fullName = "Ada Lovelace") =>
        InvitationEmail.For(email, fullName, Link, ExpiresAtUtc);
}
