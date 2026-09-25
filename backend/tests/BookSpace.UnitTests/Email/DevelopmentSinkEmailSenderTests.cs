using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Email;
using BookSpace.UnitTests.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BookSpace.UnitTests.Email;

// The sink is not a test double — it is how the invitation flow runs on a
// developer machine (docs/user-management-plan.md §5, phase 1), so it gets the
// same treatment as production code.
//
// Two of these tests are about a rule rather than a feature: the log must not
// carry the body (an invitation body contains a live activation token) and must
// not carry the recipient's address (a personal record) — CLAUDE.md §4.4.
public class DevelopmentSinkEmailSenderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "BookSpace.UnitTests.EmailSink",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        // One test deliberately makes the sink path a file rather than a
        // directory, to see the write refused.
        if (File.Exists(_directory))
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public async Task Send_ReportsDelivered()
    {
        var result = await Sender().SendAsync(Message(), CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.Null(result.FailureDetail);
    }

    [Fact]
    public async Task Send_CreatesTheDirectoryItWasPointedAt()
    {
        Assert.False(Directory.Exists(_directory));

        await Sender().SendAsync(Message(), CancellationToken.None);

        Assert.True(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Send_WritesOneEmlFile()
    {
        await Sender().SendAsync(Message(), CancellationToken.None);

        Assert.Single(Directory.GetFiles(_directory, "*.eml"));
    }

    // The file is only worth writing if a mail client can open it, which is the
    // entire reason the sink produces an .eml rather than a text dump.
    [Fact]
    public async Task TheWrittenFile_LoadsAsARealMessage()
    {
        await Sender().SendAsync(Message(), CancellationToken.None);

        var reloaded = await MimeMessage.LoadAsync(Directory.GetFiles(_directory, "*.eml").Single());

        Assert.Equal("You have been invited to BookSpace", reloaded.Subject);
        Assert.Equal("ada@acme.example", Assert.IsType<MailboxAddress>(Assert.Single(reloaded.To)).Address);
        Assert.Equal("no-reply@bookspace.example", Assert.IsType<MailboxAddress>(Assert.Single(reloaded.From)).Address);
        Assert.Contains(ActivationLink, reloaded.TextBody);
    }

    // CLAUDE.md §4.4: the body carries a live activation token, and a token in
    // a log is a credential in a log. It stays in the file.
    [Fact]
    public async Task TheLog_NeverCarriesTheBody()
    {
        var logger = new RecordingLogger<DevelopmentSinkEmailSender>();

        await Sender(logger).SendAsync(Message(), CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(ActivationLink, StringComparison.Ordinal));
    }

    // Same rule, second half: a recipient's address is a personal record. The
    // file beside the log line has it, which is where a developer was going
    // anyway.
    [Fact]
    public async Task TheLog_NeverCarriesTheRecipientAddress()
    {
        var logger = new RecordingLogger<DevelopmentSinkEmailSender>();

        await Sender(logger).SendAsync(Message(), CancellationToken.None);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("ada@acme.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLog_SaysNothingWasSentAndWhereToLook()
    {
        var logger = new RecordingLogger<DevelopmentSinkEmailSender>();

        await Sender(logger).SendAsync(Message(), CancellationToken.None);

        var line = Assert.Single(logger.Messages);
        Assert.Contains("not sent", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_directory, line, StringComparison.Ordinal);
    }

    // IClock.UtcNow is truncated to whole seconds (CLAUDE.md §4.3), so the
    // timestamp alone is not a unique file name. Two invitations in the same
    // second must not overwrite each other.
    [Fact]
    public async Task TwoMessagesAtTheSameInstant_ProduceTwoFiles()
    {
        var sender = Sender();

        await sender.SendAsync(Message(), CancellationToken.None);
        await sender.SendAsync(Message(), CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(_directory, "*.eml").Length);
    }

    // '/' and '+' are both legal in an address's local part, and '/' would have
    // steered the write out of the sink directory back when the file name was
    // built from the recipient. It no longer is — which is why this passes
    // without a sanitizer to get right.
    [Theory]
    [InlineData("a/b@acme.example")]
    [InlineData("ada+bookspace@acme.example")]
    public async Task AnAwkwardRecipientAddress_StaysInsideTheSinkDirectory(string address)
    {
        var message = Message() with { To = new EmailAddress(address) };

        var result = await Sender().SendAsync(message, CancellationToken.None);

        Assert.True(result.Delivered);
        var file = Assert.Single(Directory.GetFiles(_directory, "*.eml", SearchOption.AllDirectories));
        Assert.Equal(_directory, Path.GetDirectoryName(file));
    }

    // The rule the file name exists to keep, pinned directly rather than only
    // through the log: no part of the recipient reaches it. This is what makes
    // logging the path safe, and it is what the first version of this class got
    // wrong.
    [Fact]
    public async Task TheFileName_CarriesNothingFromTheMessage()
    {
        await Sender().SendAsync(Message(), CancellationToken.None);

        var name = Path.GetFileName(Directory.GetFiles(_directory, "*.eml").Single());

        Assert.DoesNotContain("ada", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acme", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invited", name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("20260923-103000", name[..15]);
    }

    // The IEmailSender contract: a failure is a return value, not an exception.
    // Here the "provider" is the file system, and a path that is a file rather
    // than a directory is the cheapest way to make it refuse.
    [Fact]
    public async Task AnUnusableDirectory_FailsWithoutThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        await File.WriteAllTextAsync(_directory, "not a directory");

        var result = await Sender().SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.NotNull(result.FailureDetail);
    }

    // An unusable recipient is refused by MimeMessageFactory, and the contract
    // says the caller still gets a failed result rather than an exception.
    //
    // The empty case is the one that matters: MimeKit's own MailboxAddress
    // constructor accepts it and produces a message with an empty To, which
    // this sink would otherwise write to disk and report as delivered. The
    // bare atom is accepted by MimeKit too (it is a legal local-mailbox
    // addr-spec) and is a typo here. See EmailAddressRules.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("a@")]
    [InlineData("@acme.example")]
    public async Task AnUnusableRecipientAddress_FailsWithoutThrowing(string address)
    {
        var message = Message() with { To = new EmailAddress(address) };

        var result = await Sender().SendAsync(message, CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.NotNull(result.FailureDetail);
        Assert.Empty(Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.eml") : []);
    }

    // The branch the shipped default takes: appsettings.Development.json
    // configures "sent-emails", and it has to land beside the running API
    // rather than somewhere under bin/ — so a relative path resolves against
    // the process's current directory, which for an ASP.NET Core app is the
    // content root.
    [Fact]
    public async Task ARelativeDirectory_ResolvesAgainstTheCurrentDirectory()
    {
        var relative = Path.Combine("sent-emails-test", Guid.NewGuid().ToString("N"));
        var expected = Path.Combine(Directory.GetCurrentDirectory(), relative);

        try
        {
            var sender = SenderFor(relative);

            var result = await sender.SendAsync(Message(), CancellationToken.None);

            Assert.True(result.Delivered);
            Assert.Single(Directory.GetFiles(expected, "*.eml"));
        }
        finally
        {
            if (Directory.Exists(expected))
            {
                Directory.Delete(expected, recursive: true);
            }
        }
    }

    private const string ActivationLink = "https://localhost:4200/activate?token=HzQ8e5-secret-token";

    private DevelopmentSinkEmailSender Sender(ILogger<DevelopmentSinkEmailSender>? logger = null) =>
        SenderFor(_directory, logger);

    private static DevelopmentSinkEmailSender SenderFor(
        string directory,
        ILogger<DevelopmentSinkEmailSender>? logger = null) =>
        new(
            Options.Create(new EmailOptions
            {
                DeliveryMode = EmailDeliveryMode.DevelopmentSink,
                FromAddress = "no-reply@bookspace.example",
                FromDisplayName = "BookSpace",
                DevelopmentSink = new DevelopmentSinkOptions { Directory = directory },
            }),
            new TestClock(new DateTime(2026, 9, 23, 10, 30, 0, DateTimeKind.Utc)),
            logger ?? new RecordingLogger<DevelopmentSinkEmailSender>());

    private static EmailMessage Message() => new(
        new EmailAddress("ada@acme.example", "Ada Lovelace"),
        "You have been invited to BookSpace",
        $"Activate your account: {ActivationLink}");

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
