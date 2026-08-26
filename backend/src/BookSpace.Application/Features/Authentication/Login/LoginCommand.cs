using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Authentication.Login;

// FR-2.1. Email alone identifies the user — emails are globally unique, so no
// tenant discriminator is needed
// (docs/decisions/0010-global-email-uniqueness.md).
public sealed record LoginCommand(string Email, string Password) : IRequest<AuthenticationResult>;
