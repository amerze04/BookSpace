using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BookSpace.UnitTests.Email;

// There is no SMTP server here, so these do not test that mail arrives — they
// test the one property the rest of the package leans on:
// docs/user-management-plan.md §4.3 says creating a colleague must not fail
// because a third party is down, and that only works if a transport failure
// comes back as a failed EmailSendResult rather than as an exception the
// create handler has to remember to catch.
//
// The failure paths are exercised against a port nothing is listening on, which
// is refused immediately on the loopback interface.
public class SmtpEmailSenderTests
{
    // Port 1 on the loopback: refused, not filtered, so this does not wait for
    // a timeout. TimeoutSeconds is small anyway, in case an environment answers
    // differently.
    private const string UnreachableHost = "127.0.0.1";
    private const int UnreachablePort = 1;

    [Fact]
    public async Task AnUnreachableServer_FailsWithoutThrowing()
    {
        var result = await Sender().SendAsync(Message(), CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.NotNull(result.FailureDetail);
    }

    // The detail is for the log, not for a response body — but it has to say
    // something, or "the invitation did not send" is unanswerable afterwards.
    [Fact]
    public async Task TheFailureDetail_NamesTheUnderlyingFailure()
    {
        var result = await Sender().SendAsync(Message(), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
    }

    [Fact]
    public async Task AFailure_IsLoggedWithItsException()
    {
        var logger = new RecordingLogger<SmtpEmailSender>();

        await Sender(logger).SendAsync(Message(), CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.NotNull(entry.Exception);
    }

    // An unusable recipient is refused by MimeMessageFactory before any socket
    // is opened. Same contract: a failed result, not an exception.
    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    public async Task AnUnusableRecipientAddress_FailsWithoutThrowing(string address)
    {
        var message = Message() with { To = new EmailAddress(address) };

        var result = await Sender().SendAsync(message, CancellationToken.None);

        Assert.False(result.Delivered);
        Assert.NotNull(result.FailureDetail);
    }

    // The one thing SendAsync is allowed to throw. The caller gave up; that is
    // their decision, not a delivery failure, and swallowing it would turn an
    // aborted request into a silent "we tried".
    [Fact]
    public async Task AnAlreadyCancelledToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sender().SendAsync(Message(), cts.Token));
    }

    private static SmtpEmailSender Sender(ILogger<SmtpEmailSender>? logger = null) =>
        new(
            Options.Create(new EmailOptions
            {
                DeliveryMode = EmailDeliveryMode.Smtp,
                FromAddress = "no-reply@bookspace.example",
                FromDisplayName = "BookSpace",
                Smtp = new SmtpEmailOptions
                {
                    Host = UnreachableHost,
                    Port = UnreachablePort,
                    Security = SmtpSecurity.None,
                    TimeoutSeconds = 5,
                },
            }),
            logger ?? NullLogger<SmtpEmailSender>.Instance);

    private static EmailMessage Message() => new(
        new EmailAddress("ada@acme.example", "Ada Lovelace"),
        "You have been invited to BookSpace",
        "Activate your account: https://example/activate?token=abc");

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
