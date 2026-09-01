using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Authentication.Logout;

// FR-2.4 "sessions can be revoked". Returns Unit: logout is deliberately silent
// about whether the token existed, so it can't be used to probe for live tokens.
public sealed record LogoutCommandRequest(string RefreshToken) : IRequest<Unit>;
