using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Authentication.Activate;

// Redeems an invitation: the recipient presents the token from their email and
// chooses their first password.
//
// Returns Unit rather than a token pair, and that is a real choice worth
// stating. Handing back a session here would mean re-implementing login's
// account-state gate — FR-2.4's IsActive and OrganizationStatus checks — in a
// second place, or skipping it and signing somebody into a suspended
// organization. The client already holds the password it just set, so it can
// call POST /auth/login itself; minting sessions stays in the one handler that
// knows all the rules about when not to.
public sealed record ActivateAccountCommandRequest(string Token, string Password) : IRequest<Unit>;
