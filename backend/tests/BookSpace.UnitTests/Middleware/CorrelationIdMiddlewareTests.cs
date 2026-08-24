using BookSpace.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace BookSpace.UnitTests.Middleware;

public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_NoInboundHeader_GeneratesCorrelationId()
    {
        var context = new DefaultHttpContext();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        var header = context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString();
        Assert.False(string.IsNullOrWhiteSpace(header));
        Assert.Equal(header, context.TraceIdentifier);
    }

    [Fact]
    public async Task InvokeAsync_InboundHeaderPresent_EchoesItUnchanged()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "manual-test-123";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);

        Assert.Equal("manual-test-123", context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString());
        Assert.Equal("manual-test-123", context.TraceIdentifier);
    }
}
