using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.DeactivateUser;

// POST /users/{id}/deactivate. TenantAdmin only.
//
// FR-2.4 — "a suspended user loses access immediately on next token refresh" —
// and PRD §2's Tenant Administrator persona. This is the write that flag was
// always waiting for: the auth stack has honoured IsActive since WP-2 (login
// refuses, refresh refuses *and* revokes the whole token family), and until now
// nothing outside SeedData could set it.
//
// POST /{id}/deactivate rather than PATCH, matching POST /resources/{id}/archive
// — a state transition, not an edit, and it takes no payload. Returns the user's
// own representation so a client sees isActive flip rather than re-reading.
//
// **Two things it deliberately does not do**, both of which will look like bugs
// to anyone who tries them and are therefore the screen's job to say (phase 7):
// it does not cancel the person's bookings (§4.7 — a room booked for a meeting
// that is still happening should stay booked), and it does not invalidate their
// current access token, which keeps working for up to 15 minutes (§4.4 — that
// is precisely what FR-2.4 asks for, not a gap).
public sealed record DeactivateUserCommandRequest(Guid UserId)
    : IRequest<DeactivateUserCommandResponse>;
