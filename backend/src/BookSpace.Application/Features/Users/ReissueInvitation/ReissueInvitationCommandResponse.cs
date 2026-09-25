namespace BookSpace.Application.Features.Users.ReissueInvitation;

// The 200 body of POST /users/{id}/invitation.
//
// **No activation link here either**, for exactly the reason
// CreateUserCommandResponse no longer carries one (hardening pass, 2026-09-25,
// finding 2) — a reissue response handing the admin a fresh credential would
// reopen the same hole this pass closed, just on a second endpoint.
//
// InvitationEmailSent and ActivationLinkExpiresAtUtc carry the same meaning
// they do on creation: whether the message went out, and how long the new
// link is good for.
public sealed record ReissueInvitationCommandResponse(
    Guid Id,
    string Email,
    string FullName,
    DateTime ActivationLinkExpiresAtUtc,
    bool InvitationEmailSent);
