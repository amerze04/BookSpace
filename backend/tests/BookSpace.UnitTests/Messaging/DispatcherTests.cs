using BookSpace.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.UnitTests.Messaging;

public class DispatcherTests
{
    [Fact]
    public async Task Send_HandlerRegistered_InvokesHandlerAndReturnsResult()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestHandler<PingRequest, string>, PingHandler>();
        services.AddScoped<ISender, Dispatcher>();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var result = await sender.Send(new PingRequest("hello"));

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task Send_NoHandlerRegistered_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISender, Dispatcher>();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Send(new PingRequest("hello")));
    }

    [Fact]
    public async Task Send_MultipleBehaviorsRegistered_RunInRegistrationOrderWithFirstRegisteredOutermost()
    {
        var callOrder = new List<string>();

        var services = new ServiceCollection();
        services.AddScoped<IRequestHandler<PingRequest, string>, PingHandler>();
        services.AddSingleton(callOrder);
        services.AddScoped<IPipelineBehavior<PingRequest, string>>(_ => new RecordingBehavior("First", callOrder));
        services.AddScoped<IPipelineBehavior<PingRequest, string>>(_ => new RecordingBehavior("Second", callOrder));
        services.AddScoped<ISender, Dispatcher>();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        await sender.Send(new PingRequest("hello"));

        Assert.Equal(
            new[] { "First:before", "Second:before", "Second:after", "First:after" },
            callOrder);
    }

    [Fact]
    public async Task Send_BehaviorDoesNotCallNext_HandlerIsNeverInvoked()
    {
        var services = new ServiceCollection();
        var handler = new PingHandler();
        services.AddScoped<IRequestHandler<PingRequest, string>>(_ => handler);
        services.AddScoped<IPipelineBehavior<PingRequest, string>, ShortCircuitingBehavior>();
        services.AddScoped<ISender, Dispatcher>();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var result = await sender.Send(new PingRequest("hello"));

        Assert.Equal("short-circuited", result);
        Assert.False(handler.WasInvoked);
    }

    private sealed record PingRequest(string Message) : IRequest<string>;

    private sealed class PingHandler : IRequestHandler<PingRequest, string>
    {
        public bool WasInvoked { get; private set; }

        public Task<string> Handle(PingRequest request, CancellationToken cancellationToken)
        {
            WasInvoked = true;
            return Task.FromResult(request.Message);
        }
    }

    private sealed class RecordingBehavior : IPipelineBehavior<PingRequest, string>
    {
        private readonly string _name;
        private readonly List<string> _callOrder;

        public RecordingBehavior(string name, List<string> callOrder)
        {
            _name = name;
            _callOrder = callOrder;
        }

        public async Task<string> Handle(PingRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            _callOrder.Add($"{_name}:before");
            var result = await next();
            _callOrder.Add($"{_name}:after");
            return result;
        }
    }

    private sealed class ShortCircuitingBehavior : IPipelineBehavior<PingRequest, string>
    {
        public Task<string> Handle(PingRequest request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            return Task.FromResult("short-circuited");
        }
    }
}
