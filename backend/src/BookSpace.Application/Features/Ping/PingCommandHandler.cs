using BookSpace.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace BookSpace.Application.Features.Ping;

public sealed class PingCommandHandler : IRequestHandler<PingCommand, Unit>
{
    private readonly ILogger<PingCommandHandler> _logger;

    public PingCommandHandler(ILogger<PingCommandHandler> logger)
    {
        _logger = logger;
    }

    public Task<Unit> Handle(PingCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ping received: {Message}", request.Message);
        return Task.FromResult(Unit.Value);
    }
}
