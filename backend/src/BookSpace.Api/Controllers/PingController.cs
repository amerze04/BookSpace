using BookSpace.Application.Features.Ping;
using BookSpace.Application.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace BookSpace.Api.Controllers;

// Temporary proof-of-concept for the mediator pipeline (WP-2). Delete this
// controller and BookSpace.Application/Features/Ping once login is built as
// the first real handler dispatched through the mediator.
[ApiController]
[Route("ping")]
public sealed class PingController : ControllerBase
{
    private readonly ISender _sender;

    public PingController(ISender sender)
    {
        _sender = sender;
    }

    public sealed record PingRequest(string Message);

    [HttpPost]
    public async Task<IActionResult> Post(PingRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(new PingCommand(request.Message), cancellationToken);
        return Ok();
    }
}
