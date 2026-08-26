using BookSpace.Application.Features.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Api.ExceptionHandling;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, reasonCode) = Map(exception);

        if (statusCode >= 500)
        {
            _logger.LogError(
                exception,
                "Unhandled exception on {Method} {Path} ({ReasonCode})",
                httpContext.Request.Method,
                httpContext.Request.Path,
                reasonCode);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "Handled exception on {Method} {Path} ({ReasonCode})",
                httpContext.Request.Method,
                httpContext.Request.Path,
                reasonCode);
        }

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
        };
        problemDetails.Extensions["reasonCode"] = reasonCode;

        if (exception is FluentValidation.ValidationException validationException)
        {
            problemDetails.Extensions["errors"] = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        }

        httpContext.Response.StatusCode = statusCode;

        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception,
        });
    }

    // Known mappings today are deliberately limited to what already exists in the
    // codebase (WP-2 stream 3 is a safety net, not the full AC). Extend this switch,
    // don't add parallel handling elsewhere, when:
    //   - booking rejections exist: map whatever exception type carries CLAUDE.md §6's
    //     reason codes (SlotUnavailable, CapacityExceeded, OutsideAvailability,
    //     BlackoutPeriod, ResourceArchived, ApprovalRequired) -> 409/400 as appropriate.
    private static (int StatusCode, string Title, string ReasonCode) Map(Exception exception) => exception switch
    {
        // FR-2.1 / FR-2.2. The reason code comes from the exception rather than
        // being decided here, because only the handler knows whether this was a
        // bad credential, an expired token, or detected reuse.
        AuthenticationException authenticationException => (
            StatusCodes.Status401Unauthorized,
            "Authentication failed.",
            authenticationException.ReasonCode),
        DbUpdateConcurrencyException => (
            StatusCodes.Status409Conflict,
            "The record was modified by another request.",
            "ConcurrencyConflict"),
        FluentValidation.ValidationException => (
            StatusCodes.Status400BadRequest,
            "One or more validation errors occurred.",
            "ValidationFailed"),
        _ => (
            StatusCodes.Status500InternalServerError,
            "An unexpected error occurred.",
            "UnexpectedError"),
    };
}
