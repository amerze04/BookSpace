using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Authentication.Refresh;

// FR-2.1 / FR-2.2. The raw refresh token, exactly as login handed it out.
public sealed record RefreshTokenCommandRequest(string RefreshToken) : IRequest<RefreshTokenCommandResponse>;
