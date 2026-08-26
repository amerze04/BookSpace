using BookSpace.Application.Messaging;
using BookSpace.Application.Messaging.Behaviors;
using Microsoft.Extensions.Logging;

namespace BookSpace.UnitTests.Messaging;

public class LoggingBehaviorTests
{
    [Fact]
    public async Task Handle_DelegatesToNext_ReturnsItsResult()
    {
        var behavior = new LoggingBehavior<PingRequest, string>(new CountingLogger<LoggingBehavior<PingRequest, string>>());

        var result = await behavior.Handle(new PingRequest("hello"), () => Task.FromResult("hello"), CancellationToken.None);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task Handle_LogsStartAndCompletion()
    {
        var logger = new CountingLogger<LoggingBehavior<PingRequest, string>>();
        var behavior = new LoggingBehavior<PingRequest, string>(logger);

        await behavior.Handle(new PingRequest("hello"), () => Task.FromResult("hello"), CancellationToken.None);

        Assert.Equal(2, logger.LogCount);
    }

    [Fact]
    public async Task Handle_NextThrows_StillLogsCompletionAndRethrows()
    {
        var logger = new CountingLogger<LoggingBehavior<PingRequest, string>>();
        var behavior = new LoggingBehavior<PingRequest, string>(logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(
            new PingRequest("hello"),
            () => throw new InvalidOperationException("boom"),
            CancellationToken.None));

        Assert.Equal(2, logger.LogCount);
    }

    private sealed record PingRequest(string Message) : IRequest<string>;

    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int LogCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LogCount++;
        }
    }
}
