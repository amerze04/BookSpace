using BookSpace.Application.Abstractions;
using MimeKit;

namespace BookSpace.Infrastructure.Email;

// The one place an EmailMessage becomes a MIME message, shared by both senders
// on purpose: what the development sink writes to disk is what the SMTP sender
// would have put on the wire. A sink that built its own rendering would be a
// second implementation, and the one nobody watches is the one that drifts — so
// "it looked right in the .eml" would stop being evidence about the real path.
internal static class MimeMessageFactory
{
    // Throws on an unusable address, which is why both senders call it inside
    // the try/catch that turns any failure into a failed EmailSendResult:
    // IEmailSender promises never to throw for a delivery failure, and a
    // recipient this cannot address is one.
    //
    // The check is explicit rather than left to MimeKit, because MimeKit's
    // MailboxAddress constructor accepts an empty address without complaint —
    // which would produce a message with an empty To that the sink writes to
    // disk and reports as delivered. See EmailAddressRules.
    public static MimeMessage Create(EmailOptions options, EmailMessage message)
    {
        if (!EmailAddressRules.IsUsable(message.To.Address))
        {
            throw new ArgumentException(
                $"'{message.To.Address}' is not a usable recipient address.",
                nameof(message));
        }

        var mime = new MimeMessage();

        mime.From.Add(new MailboxAddress(options.FromDisplayName, options.FromAddress));
        mime.To.Add(new MailboxAddress(message.To.DisplayName, message.To.Address));
        mime.Subject = message.Subject;

        // Hardening pass, finding 4: a deterministic Message-ID for a
        // caller that supplies one (NotificationRepository, keyed on the
        // notification row's own id), so a repeated send of the *same* row
        // — whether an ordinary retry or the rarer post-send-crash case —
        // carries identical headers rather than MimeMessage's own default of
        // a fresh random one per construction. See EmailMessage's own header
        // for what this can and cannot promise. Left unset (MimeKit
        // generates its usual random one) for every caller that passes
        // nothing, which is every caller but the dispatch job today.
        if (!string.IsNullOrWhiteSpace(message.IdempotencyKey))
        {
            var domain = options.FromAddress.Contains('@')
                ? options.FromAddress[(options.FromAddress.IndexOf('@') + 1)..]
                : "bookspace.local";
            mime.MessageId = $"{message.IdempotencyKey}@{domain}";
        }

        var body = new BodyBuilder { TextBody = message.TextBody };

        // Left unset rather than defaulted from the text body. A multipart/
        // alternative whose two halves carry the same markup is noise, and the
        // caller deciding it has no HTML version is not the same as it having
        // forgotten one.
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            body.HtmlBody = message.HtmlBody;
        }

        mime.Body = body.ToMessageBody();

        return mime;
    }
}
