using BookSpace.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Email;

// Writes each message to disk as an .eml file and sends nothing.
//
// Not a test double — this is how the invitation flow runs on the owner's
// machine, where there is no provider account. An .eml rather than a text dump
// because it opens in any mail client, so the activation link is clicked rather
// than copied out of a log, and because MimeMessageFactory produces it: what is
// on disk is what SMTP would have sent.
//
// CLAUDE.md §4.4 governs what reaches the log, and here it matters more than
// usual: an invitation body contains a live activation token. So the log line
// carries the subject and the file path and nothing else — not the body, and
// not the recipient's address, which is a personal record. Both are in the
// file, one `cat` away, and the file is the thing a developer wanted anyway.
//
// Those files hold usable credentials. The directory is gitignored, and a
// deployment that selects this mode by accident is storing invitations on a
// disk instead of sending them — which is why EmailOptions defaults to Smtp.
internal sealed class DevelopmentSinkEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<DevelopmentSinkEmailSender> _logger;
    private readonly string _directory;

    public DevelopmentSinkEmailSender(
        IOptions<EmailOptions> options,
        IClock clock,
        ILogger<DevelopmentSinkEmailSender> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;

        // Resolved once, against the process's current directory, which for an
        // ASP.NET Core app is the content root — so the configured default
        // lands in backend/src/BookSpace.Api/sent-emails rather than somewhere
        // under bin/.
        var configured = _options.DevelopmentSink.Directory;
        _directory = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(Directory.GetCurrentDirectory(), configured);
    }

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            var mime = MimeMessageFactory.Create(_options, message);

            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, FileNameFor());

            await using (var stream = File.Create(path))
            {
                await mime.WriteToAsync(stream, cancellationToken);
            }

            _logger.LogInformation(
                "Email not sent: delivery mode is DevelopmentSink. Subject {Subject} written to {EmailFilePath}.",
                message.Subject,
                path);

            return EmailSendResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Same contract as the SMTP sender: an unwritable directory or a
            // full disk is a delivery failure, not an exception the caller has
            // to know about. Without this the two senders would behave
            // differently on failure, and the one the developer sees every day
            // would be the lenient one.
            _logger.LogError(ex, "Writing an email to the development sink at {EmailDirectory} failed.", _directory);

            return EmailSendResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // A timestamp and random bytes, and deliberately nothing from the message.
    //
    // The recipient's address was in this name first, and it was a mistake
    // caught by this class's own test: the log line carries the path, so an
    // address in the file name is an address in the log, and the §4.4 rule
    // above would have been a comment describing something that was not true.
    // Leaving the address out makes the file name carry no untrusted input at
    // all, which is a better answer than sanitizing it — there is no
    // path-traversal case left to get wrong.
    //
    // The random suffix is what keeps two invitations sent in the same second
    // from overwriting each other: IClock.UtcNow is truncated to whole seconds
    // (CLAUDE.md §4.3), so the timestamp alone is not unique.
    private string FileNameFor() =>
        $"{_clock.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml";
}
