using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.CreateUser;

// The 201 body of POST /users.
//
// **ActivationLink used to be here, and it is gone deliberately (hardening
// pass, 2026-09-25, finding 2).** It was a live credential in a response body
// — the raw activation link, carried whether or not the invitation email went
// out — and the design argument for that was "the admin is standing right
// there; they can pass it on by chat or in person". The flaw the pass found:
// an administrator holding the invitee's own activation link can redeem it
// first, set the password themselves, and sign in *as the person they just
// created* before that person ever sees the invitation. There is no wire
// distinction between "the admin needs this because delivery failed" and "the
// admin is choosing to read someone else's credential" — the capability is the
// same either way, so it is removed rather than left as a halfway state.
//
// The account is never stranded by this: ReissueInvitationCommandResponse
// (`POST /users/{id}/invitation`) is the recovery path when delivery fails or
// the recipient loses the email, and it is safe to call as many times as
// needed — see ReissueInvitationCommandRequestHandler for why an earlier
// outstanding token is superseded each time rather than left live alongside
// the new one.
//
// InvitationEmailSent stays: the screen still needs to say whether the message
// went out, even though it can no longer hand over the fallback itself.
//
// ActivationLinkExpiresAtUtc stays too — a date, not a credential, and the
// screen can still say how long the invitation is good for.
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
    DateTime ActivationLinkExpiresAtUtc,
    bool InvitationEmailSent);
