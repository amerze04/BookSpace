using BookSpace.Application.Features.Authentication;
using BookSpace.Application.Features.Authentication.Login;
using BookSpace.Application.Features.Authentication.Logout;
using BookSpace.Application.Features.Authentication.Refresh;
using BookSpace.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BookSpace.Api.Controllers;

// FR-2.1 / FR-2.2 / FR-2.4. Thin by design (WP-2: "no business logic lives in a
// controller") — each action maps the request onto a command, dispatches it
// through the mediator, and shapes the HTTP result. Every decision about
// credentials, rotation, and reuse lives in the handlers.
//
// AllowAnonymous because these endpoints are how a caller *becomes*
// authenticated; the fallback policy would otherwise make them unreachable.
[ApiController]
[Route("auth")]
[AllowAnonymous]
public sealed class AuthController : ControllerBase
{
    private readonly ISender _sender;

    public AuthController(ISender sender)
    {
        _sender = sender;
    }

    public sealed record LoginRequest(string Email, string Password);

    // The refresh token travels in the body rather than an httpOnly cookie —
    // tradeoff recorded in docs/decisions/0011, to revisit when the Angular app
    // lands at M4.
    public sealed record RefreshRequest(string RefreshToken);

    // Hardening pass, P2 security: credential-stuffing/guessing throttle.
    // See Program.cs's AddRateLimiter for the policy itself.
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    [ProducesResponseType<LoginCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new LoginCommandRequest(request.Email, request.Password), cancellationToken);
        return Ok(result);
    }

    // Hardening pass, P2 security: throttles guessing at refresh tokens.
    // A higher limit than login's, deliberately — a real client refreshes
    // routinely as part of normal use, not just when a person is typing.
    [HttpPost("refresh")]
    [EnableRateLimiting("refresh")]
    [ProducesResponseType<RefreshTokenCommandResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new RefreshTokenCommandRequest(request.RefreshToken), cancellationToken);
        return Ok(result);
    }

    // 204 whether or not the token was recognized — see LogoutCommandRequestHandler.
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(new LogoutCommandRequest(request.RefreshToken), cancellationToken);
        return NoContent();
    }
}
