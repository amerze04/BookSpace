using System.Text.Json;
using BookSpace.Api.ExceptionHandling;
using BookSpace.Application.Common.Errors;
using BookSpace.Application.Features.Authentication;
using FluentValidation.Results;
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

    [Fact]
    public async Task TryHandleAsync_ValidationException_Returns400WithValidationFailedReasonCode()
    {
        var exception = new FluentValidation.ValidationException(new[]
        {
            new ValidationFailure("Message", "must not be empty"),
        });

        var (handled, statusCode, body, _) = await InvokeAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Equal("ValidationFailed", body.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task TryHandleAsync_ValidationException_IncludesPerFieldErrors()
    {
        var exception = new FluentValidation.ValidationException(new[]
        {
            new ValidationFailure("Message", "must not be empty"),
            new ValidationFailure("Message", "must be at least 3 characters"),
            new ValidationFailure("Slug", "already exists"),
        });

        var (_, _, body, _) = await InvokeAsync(exception);

        var errors = body.GetProperty("errors");
        var messageErrors = errors.GetProperty("Message").EnumerateArray().Select(e => e.GetString()).ToArray();
        var slugErrors = errors.GetProperty("Slug").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "must not be empty", "must be at least 3 characters" }, messageErrors);
        Assert.Equal(new[] { "already exists" }, slugErrors);
    }

    // FR-2.1 / FR-2.2: the reason code is carried by the exception, not decided
    // here, so each authentication failure keeps the meaning the handler gave it.
    [Theory]
    [InlineData("InvalidCredentials")]
    [InlineData("RefreshTokenReuseDetected")]
    [InlineData("RefreshTokenExpired")]
    [InlineData("AccountInactive")]
    public async Task TryHandleAsync_AuthenticationException_Returns401WithTheHandlersReasonCode(string reasonCode)
    {
        var exception = new AuthenticationException(reasonCode, "authentication failed");

        var (handled, statusCode, body, _) = await InvokeAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, statusCode);
        Assert.Equal(reasonCode, body.GetProperty("reasonCode").GetString());
    }

    // The message is for the server log; it must not reach the client, where it
    // could distinguish "no such account" from "wrong password".
    [Fact]
    public async Task TryHandleAsync_AuthenticationException_DoesNotLeakTheExceptionMessage()
    {
        var exception = new AuthenticationException("InvalidCredentials", "no user for member@acme.test");

        var (_, _, body, _) = await InvokeAsync(exception);

        Assert.DoesNotContain("member@acme.test", body.ToString());
    }

    [Fact]
    public async Task TryHandleAsync_AuthenticationException_LogsAsWarningNotError()
    {
        // 401 is a client problem, so it logs once at Warning — a failed login is
        // not a server fault and must not page anyone.
        var (_, _, _, logCount) = await InvokeAsync(
            new AuthenticationException("InvalidCredentials", "bad password"));

        Assert.Equal(1, logCount);
    }

    // WP-3 Phase 1: one AppException case, mapped by Kind. A new failure needs a
    // reason code and a Kind, not a new switch case — these four rows are the
    // whole contract WP-4's booking rejections will arrive through.
    [Theory]
    [InlineData(ErrorKind.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorKind.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorKind.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorKind.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorKind.RuleViolation, StatusCodes.Status422UnprocessableEntity)]
    public async Task TryHandleAsync_AppException_MapsKindToStatusCode(ErrorKind kind, int expectedStatusCode)
    {
        var exception = new AppException(kind, "SomeReasonCode", "internal detail");

        var (handled, statusCode, body, _) = await InvokeAsync(exception);

        Assert.True(handled);
        Assert.Equal(expectedStatusCode, statusCode);
        Assert.Equal("SomeReasonCode", body.GetProperty("reasonCode").GetString());
    }

    // The message is the log's, not the client's — for every Kind, not just
    // authentication. Nothing in AppException decides what is safe to disclose,
    // so nothing is disclosed.
    [Theory]
    [InlineData(ErrorKind.NotFound)]
    [InlineData(ErrorKind.Conflict)]
    [InlineData(ErrorKind.RuleViolation)]
    public async Task TryHandleAsync_AppException_DoesNotLeakTheExceptionMessage(ErrorKind kind)
    {
        var exception = new AppException(kind, "SomeReasonCode", "resource 4f2c belongs to org globex");

        var (_, _, body, _) = await InvokeAsync(exception);

        Assert.DoesNotContain("globex", body.ToString());
        Assert.DoesNotContain("4f2c", body.ToString());
    }

    // 4xx is the client's problem and logs at Warning; only a 5xx is the
    // server's fault. A rule violation must not page anyone.
    [Fact]
    public async Task TryHandleAsync_AppException_LogsExactlyOnce()
    {
        var (_, _, _, logCount) = await InvokeAsync(
            new AppException(ErrorKind.RuleViolation, "ResourceArchived", "resource is archived"));

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
