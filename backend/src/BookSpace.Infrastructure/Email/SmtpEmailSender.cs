using BookSpace.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Email;

// The real sender. SMTP rather than a vendor SDK, deliberately: there is no
// provider account yet (docs/user-management-plan.md §2), and every
// transactional provider — SendGrid, Postmark, Mailgun, Brevo — offers an SMTP
// relay that takes the API key as the password. So this works against whichever
// one is chosen later, and picking one now would be a dependency taken before
// the decision it depends on.
//
// MailKit rather than System.Net.Mail.SmtpClient, which Microsoft's own
// documentation tells you not to use for new development.
//
// A connection per send, not a pooled one. Invitations are sent one at a time,
// as a side effect of an admin creating a colleague; a long-lived connection
// would be state to keep healthy across idle periods for no measurable gain.
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        var smtp = _options.Smtp;

        try
        {
            var mime = MimeMessageFactory.Create(_options, message);

            using var client = new SmtpClient
            {
                Timeout = (int)TimeSpan.FromSeconds(smtp.TimeoutSeconds).TotalMilliseconds,
            };

            await client.ConnectAsync(smtp.Host, smtp.Port, SocketOptionsFor(smtp.Security), cancellationToken);

            // Skipped entirely when no credential is configured — an
            // unauthenticated relay is a legitimate deployment, and calling
            // AuthenticateAsync with empty strings would fail it instead.
            // EmailOptionsValidator has already refused one of the pair without
            // the other; both are tested here anyway rather than papered over
            // with a null-forgiving operator, so a validator that ever stopped
            // catching it degrades to "connected anonymously" instead of a
            // NullReferenceException.
            if (!string.IsNullOrWhiteSpace(smtp.Username) && !string.IsNullOrWhiteSpace(smtp.Password))
            {
                await client.AuthenticateAsync(smtp.Username, smtp.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            return EmailSendResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up — their decision, not a delivery failure, and
            // the one thing this method is allowed to throw. MailKit's own
            // Timeout surfaces as a cancellation too, which is why the guard
            // tests the caller's token rather than catching the type alone.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad. The contract is that a delivery failure never
            // throws (see IEmailSender), and MailKit's failure surface is a
            // refused socket, a TLS handshake, an auth rejection, a protocol
            // error and a timeout across five different exception types — an
            // enumerated catch list is a list with something missing from it.
            //
            // Logged with the exception rather than only returned, because the
            // caller is going to reduce this to "the invitation did not send"
            // for the admin and the detail has to survive somewhere.
            _logger.LogError(
                ex,
                "SMTP delivery failed via {SmtpHost}:{SmtpPort}.",
                smtp.Host,
                smtp.Port);

            return EmailSendResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static SecureSocketOptions SocketOptionsFor(SmtpSecurity security) => security switch
    {
        // StartTls, not Auto or StartTlsWhenAvailable: both of those fall back
        // to plaintext against a server that does not advertise STARTTLS,
        // which is precisely the case this setting exists to refuse. A
        // deployment that genuinely wants an unencrypted relay says None.
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, "Unsupported SMTP security mode."),
    };
}
