using System.Text.Json;
using BookSpace.Api.ExceptionHandling;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BookSpace.UnitTests.ExceptionHandling;

public class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_UnmappedException_Returns500WithUnexpectedErrorReasonCode()
    {
        var (handled, statusCode, body, _) = await InvokeAsync(new InvalidOperationException("boom"));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, statusCode);
        Assert.Equal("UnexpectedError", body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_DbUpdateConcurrencyException_Returns409WithConcurrencyConflictReasonCode()
    {
        var (handled, statusCode, body, _) = await InvokeAsync(new DbUpdateConcurrencyException("stale row"));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, statusCode);
        Assert.Equal("ConcurrencyConflict", body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_AnyException_LogsExactlyOnce()
    {
        var (_, _, _, logCount) = await InvokeAsync(new InvalidOperationException("boom"));

        Assert.Equal(1, logCount);
    }

    private static async Task<(bool Handled, int StatusCode, JsonElement Body, int LogCount)> InvokeAsync(Exception exception)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        var provider = services.BuildServiceProvider();

        var testLogger = new CountingLogger<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(testLogger);

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        var body = JsonDocument.Parse(json).RootElement;

        return (handled, context.Response.StatusCode, body, testLogger.LogCount);
    }

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
