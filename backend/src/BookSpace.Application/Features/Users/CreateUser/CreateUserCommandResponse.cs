using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.CreateUser;

// The 201 body of POST /users.
//
// **ActivationLink is the unusual part, and it is deliberate.** It is a live
// credential in a response body, carried whether or not the invitation email
// went out (docs/user-management-plan.md §4.3). The alternative was to fail the
// whole request when the provider is down — coupling "add a colleague" to a
// third party's uptime, and needing a rollback — or to create the account and
// leave the recipient with no route in at all, since re-issuing an invitation is
// out of scope (§3.4, §6). So the admin is handed the link and can pass it on by
// chat or in person, which is what they would do anyway in a small organization.
//
// It follows that this response must be shown once and not stored. That is the
// frontend's job (phase 6) and it is the reason InvitationEmailSent is here
// rather than inferred: the screen reads differently when the email failed, and
// a client cannot tell from a 201 alone.
//
// ActivationLinkExpiresAtUtc so the screen can say how long the link is good
// for. Absolute — nothing extends it and nothing re-issues it.
//
// Roles is included even though the caller did not send any, because the
// handler assigns Member and the admin should see what they just created rather
// than have to re-read it. Serialized by name ("Member", not 3).
//
// What is deliberately absent: the placeholder password hash (obviously), and
// anything about *why* an email failed — IEmailSender's FailureDetail can name
// hosts and accounts and belongs in the log.
public sealed record CreateUserCommandResponse(
    Guid Id,
    string Email,
    string FullName,
    bool IsActive,
    IReadOnlyList<Role> Roles,
    DateTime CreatedAtUtc,
    string ActivationLink,
    DateTime ActivationLinkExpiresAtUtc,
    bool InvitationEmailSent);
