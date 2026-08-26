using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Ping;

// Temporary proof-of-concept for the mediator pipeline (WP-2). Delete this
// folder and PingController once login is built as the first real handler.
public sealed record PingCommand(string Message) : IRequest<Unit>;
