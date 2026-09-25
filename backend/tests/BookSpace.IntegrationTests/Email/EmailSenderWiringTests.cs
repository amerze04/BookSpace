using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Email;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BookSpace.IntegrationTests.Email;

// User management phase 1 has no endpoint, so this is where its wiring is
// actually exercised: real configuration binding, real EmailOptionsValidator
// running under ValidateOnStart, and the real registration switch in
// AddInfrastructure picking a sender from a configured delivery mode.
//
// None of that is reachable from a unit test — the unit tests construct the
// senders directly and never go through IConfiguration or the DI container,
// which is exactly where a typo in a section name or a broken switch would
// live.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class EmailSenderWiringTests
{
    private readonly AuthenticationTestHost _host;

    public EmailSenderWiringTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    // The host boots at all, which is the ValidateOnStart claim: EmailOptions
    // is validated at startup, and this host's configuration satisfies it.
    [Fact]
    public void TheConfiguredDeliveryMode_SelectsTheMatchingSender()
    {
        using var scope = _host.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        Assert.IsType<DevelopmentSinkEmailSender>(sender);
    }

    [Fact]
    public void EmailOptions_BindFromConfiguration()
    {
        using var scope = _host.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptions<EmailOptions>>().Value;

        Assert.Equal(EmailDeliveryMode.DevelopmentSink, options.DeliveryMode);
        Assert.Equal("no-reply@bookspace.test", options.FromAddress);
        Assert.Equal("BookSpace Integration Tests", options.FromDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(options.DevelopmentSink.Directory));
    }

    // End to end through the container: a message handed to the resolved
    // IEmailSender lands on disk as a message a mail client can open. Phase 3's
    // invitation is this call with a real body.
    [Fact]
    public async Task TheResolvedSender_WritesAMessageAMailClientCouldOpen()
    {
        using var scope = _host.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var directory = scope.ServiceProvider
            .GetRequiredService<IOptions<EmailOptions>>().Value.DevelopmentSink.Directory;

        var subject = $"Wiring probe {Guid.NewGuid():N}";
        var result = await sender.SendAsync(
            new EmailMessage(
                new EmailAddress("ada@acme.example", "Ada Lovelace"),
                subject,
                "Activate your account: https://localhost:4200/activate?token=probe"),
            CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.Null(result.FailureDetail);

        var written = await FindBySubjectAsync(directory, subject);
        Assert.Equal("ada@acme.example", Assert.IsType<MailboxAddress>(Assert.Single(written.To)).Address);
        Assert.Equal("no-reply@bookspace.test", Assert.IsType<MailboxAddress>(Assert.Single(written.From)).Address);
        Assert.Contains("token=probe", written.TextBody);
    }

    // The sink directory is shared by the whole run, so a message is found by
    // its own unique subject rather than by being the only file there.
    private static async Task<MimeMessage> FindBySubjectAsync(string directory, string subject)
    {
        foreach (var path in Directory.GetFiles(directory, "*.eml"))
        {
            var message = await MimeMessage.LoadAsync(path);
            if (message.Subject == subject)
            {
                return message;
            }
        }

        Assert.Fail($"No message with subject '{subject}' was written to {directory}.");
        throw new InvalidOperationException("unreachable");
    }
}
