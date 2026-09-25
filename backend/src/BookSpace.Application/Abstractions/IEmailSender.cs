namespace BookSpace.Application.Abstractions;

// The first email path in the application. PRD §11 assumes a transactional
// provider is available; none was ever wired, and user management needs one
// because an invitation has to reach somebody who has no account yet
// (docs/user-management-plan.md §3.1).
//
// Deliberately *not* the Notifications table. That is a scheduler — it carries
// SendAtUtc, Attempts and LastError, no address and no body, and
// CK_Notifications_HasContext anchors every row to a booking or a series. An
// invitation has neither anchor and nothing to schedule: it is sent now, inside
// the request somebody is waiting on. See docs/user-management-plan.md §4.1 for
// the full argument, and note that the three background jobs (CLAUDE.md §7)
// will be able to use this sender when they are built.
//
// A recipient, not a list. Every message this application sends goes to one
// person; a To/Cc/Bcc model would be shape invented for a caller that does not
// exist.
public sealed record EmailAddress(string Address, string? DisplayName = null);

// TextBody is required and HtmlBody is optional, not the other way round: a
// message that renders only as HTML is unreadable in a client that refuses it,
// and an activation link has to survive that.
public sealed record EmailMessage(
    EmailAddress To,
    string Subject,
    string TextBody,
    string? HtmlBody = null);

// A delivery failure is a *return value*, not an exception, and that is the
// load-bearing part of this contract.
//
// docs/user-management-plan.md §4.3: creating a colleague must not fail because
// a third party is down, so the create handler creates the user, reports that
// the invitation did not go out, and hands the admin the activation link
// anyway. If SendAsync threw, that handling would be a try/catch a future
// caller can forget; as a result type the failure is in the signature and has
// to be discarded on purpose.
//
// FailureDetail is for the log. It carries whatever the provider said, which
// can name hosts and accounts, so it is not something to put in a response
// body — what the admin is told is the calling handler's decision.
public sealed record EmailSendResult(bool Delivered, string? FailureDetail)
{
    public static EmailSendResult Success() => new(Delivered: true, FailureDetail: null);

    public static EmailSendResult Failed(string failureDetail) =>
        new(Delivered: false, failureDetail);
}

public interface IEmailSender
{
    // Never throws for a delivery failure — a refused connection, a rejected
    // recipient, a timeout and a bad credential all come back as a failed
    // EmailSendResult. Cancellation of the caller's token still propagates,
    // because that is the caller's own decision rather than the provider's.
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
