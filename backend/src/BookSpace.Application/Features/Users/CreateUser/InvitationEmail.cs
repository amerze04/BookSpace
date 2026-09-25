using BookSpace.Application.Abstractions;

namespace BookSpace.Application.Features.Users.CreateUser;

// The first email this application composes. A pure function, separate from the
// handler, because the handler's job is the write and this is the only part
// worth reading character by character — an invitation is the one message a
// recipient judges before they trust the product.
//
// **Plain text and HTML both.** The text half is required (see EmailMessage): an
// activation link has to survive a client that refuses HTML, and this is the one
// message where failing to reach the recipient means they never get in at all.
//
// What it deliberately does not say:
//
//   - **Which organization invited them.** It would be better copy, and it needs
//     a read this handler does not otherwise do — there is no organization
//     repository, and decision `0010` means a recipient belongs to exactly one
//     tenant anyway, so the link cannot be ambiguous. Flagged for the owner
//     rather than quietly added.
//   - **Who invited them.** ICurrentUser carries an id, not a name; showing it
//     would mean another read for the same marginal gain.
//   - **Anything conditional on the recipient's roles.** They have none worth
//     describing at creation, and a message that promised access it cannot
//     grant would be worse than a neutral one.
internal static class InvitationEmail
{
    public static EmailMessage For(
        string recipientEmail,
        string recipientFullName,
        string activationLink,
        DateTime expiresAtUtc)
    {
        // "u" is ISO 8601 with a trailing Z, so the instant is unambiguous in
        // any mailbox in any timezone. Rendering it in the recipient's local
        // time is impossible here — nothing knows what that is, and a resource
        // timezone (decision `0003`) is the wrong answer for a person.
        var expiry = expiresAtUtc.ToString("u");

        var text =
            $"""
            Hello {recipientFullName},

            An administrator has created a BookSpace account for you.

            To choose a password and sign in, open this link:

            {activationLink}

            The link can be used once, and stops working after {expiry}.

            If you were not expecting this, you can ignore this message — the
            account cannot be used until someone sets a password with the link
            above.
            """;

        var html =
            $"""
            <p>Hello {HtmlEscape(recipientFullName)},</p>
            <p>An administrator has created a BookSpace account for you.</p>
            <p><a href="{HtmlEscape(activationLink)}">Choose a password and sign in</a></p>
            <p>If the link does not work, copy this address into your browser:<br>
            {HtmlEscape(activationLink)}</p>
            <p>The link can be used once, and stops working after {HtmlEscape(expiry)}.</p>
            <p>If you were not expecting this, you can ignore this message — the account
            cannot be used until someone sets a password with the link above.</p>
            """;

        return new EmailMessage(
            new EmailAddress(recipientEmail, recipientFullName),
            "You have been invited to BookSpace",
            text,
            html);
    }

    // The full name comes from an administrator typing into a form, so it is
    // untrusted input going into markup — an unescaped "<" is at best a broken
    // message and at worst a link the recipient did not expect. Hand-rolled
    // rather than pulled from System.Net.WebUtility so the Application layer
    // does not acquire an HTML dependency for five characters; these are the
    // five that matter in element content and in a double-quoted attribute.
    private static string HtmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
