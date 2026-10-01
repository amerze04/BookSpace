using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Email;
using MimeKit;

namespace BookSpace.UnitTests.Email;

// Both senders go through this, which is the point: what the development sink
// writes to disk is what SMTP would have sent. These tests pin the headers a
// developer will actually look at in the .eml and then believe about the real
// path.
public class MimeMessageFactoryTests
{
    [Fact]
    public void From_UsesTheConfiguredAddressAndDisplayName()
    {
        var mime = MimeMessageFactory.Create(Options(), Message());

        var from = Assert.IsType<MailboxAddress>(Assert.Single(mime.From));
        Assert.Equal("no-reply@bookspace.example", from.Address);
        Assert.Equal("BookSpace", from.Name);
    }

    [Fact]
    public void From_OmitsTheDisplayNameWhenNoneIsConfigured()
    {
        var options = Options();
        options.FromDisplayName = null;

        var mime = MimeMessageFactory.Create(options, Message());

        var from = Assert.IsType<MailboxAddress>(Assert.Single(mime.From));
        Assert.Equal("no-reply@bookspace.example", from.Address);
        Assert.True(string.IsNullOrEmpty(from.Name));
    }

    [Fact]
    public void To_UsesTheRecipientAddressAndDisplayName()
    {
        var mime = MimeMessageFactory.Create(Options(), Message());

        var to = Assert.IsType<MailboxAddress>(Assert.Single(mime.To));
        Assert.Equal("ada@acme.example", to.Address);
        Assert.Equal("Ada Lovelace", to.Name);
    }

    [Fact]
    public void Subject_IsCarriedThrough()
    {
        var mime = MimeMessageFactory.Create(Options(), Message());

        Assert.Equal("You have been invited to BookSpace", mime.Subject);
    }

    [Fact]
    public void TextBody_IsAlwaysPresent()
    {
        var mime = MimeMessageFactory.Create(Options(), Message());

        Assert.Equal("Activate your account: https://example/activate?token=abc", mime.TextBody);
    }

    // An HTML-only message is unreadable in a client that refuses HTML, and an
    // activation link has to survive that — so the plain-text half is the
    // required one and this is the optional addition.
    [Fact]
    public void HtmlBody_IsPresentWhenSupplied()
    {
        var message = Message() with { HtmlBody = "<p>Activate</p>" };

        var mime = MimeMessageFactory.Create(Options(), message);

        Assert.Equal("<p>Activate</p>", mime.HtmlBody);
        Assert.Equal("Activate your account: https://example/activate?token=abc", mime.TextBody);
    }

    [Fact]
    public void HtmlBody_IsAbsentWhenNotSupplied()
    {
        var mime = MimeMessageFactory.Create(Options(), Message());

        Assert.Null(mime.HtmlBody);
    }

    // Hardening pass, finding 4. Deterministic on the supplied key, not
    // randomly generated — so a repeated send of the *same* logical
    // notification (an ordinary retry, or the rarer post-send-crash case)
    // carries an identical Message-ID, which is a real mitigation for a
    // duplicate send even though nothing requires a receiving mail server to
    // act on it (see EmailMessage's own header).
    [Fact]
    public void MessageId_IsDeterministicWhenAnIdempotencyKeyIsSupplied()
    {
        var message = Message() with { IdempotencyKey = "abc123" };

        var first = MimeMessageFactory.Create(Options(), message);
        var second = MimeMessageFactory.Create(Options(), message);

        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Contains("abc123", first.MessageId);
    }

    // The domain half of the From address, not a hardcoded placeholder — so
    // the Message-ID at least names a domain this deployment actually owns.
    [Fact]
    public void MessageId_UsesTheConfiguredFromAddressDomain()
    {
        var message = Message() with { IdempotencyKey = "abc123" };

        var mime = MimeMessageFactory.Create(Options(), message);

        Assert.EndsWith("@bookspace.example", mime.MessageId);
    }

    // No caller today passes a key for most messages (activation,
    // invitation) — MimeKit's own default (a fresh random id per message)
    // is exactly right for those, and must not become deterministic by
    // accident.
    [Fact]
    public void MessageId_IsLeftToMimeKitWhenNoIdempotencyKeyIsSupplied()
    {
        var first = MimeMessageFactory.Create(Options(), Message());
        var second = MimeMessageFactory.Create(Options(), Message());

        Assert.NotEqual(first.MessageId, second.MessageId);
    }

    // Not decoration: an .eml is only worth writing if it round-trips, because
    // opening it in a mail client is the whole reason the sink produces one.
    [Fact]
    public async Task TheMessage_RoundTripsThroughAStream()
    {
        var mime = MimeMessageFactory.Create(Options(), Message() with { HtmlBody = "<p>Activate</p>" });

        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream);
        stream.Position = 0;
        var reloaded = await MimeMessage.LoadAsync(stream);

        Assert.Equal("You have been invited to BookSpace", reloaded.Subject);
        Assert.Equal("ada@acme.example", Assert.IsType<MailboxAddress>(Assert.Single(reloaded.To)).Address);
        Assert.Contains("https://example/activate?token=abc", reloaded.TextBody);
        Assert.Equal("<p>Activate</p>", reloaded.HtmlBody);
    }

    private static EmailOptions Options() => new()
    {
        FromAddress = "no-reply@bookspace.example",
        FromDisplayName = "BookSpace",
    };

    private static EmailMessage Message() => new(
        new EmailAddress("ada@acme.example", "Ada Lovelace"),
        "You have been invited to BookSpace",
        "Activate your account: https://example/activate?token=abc");
}
