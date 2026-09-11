using BookSpace.Application.Common.Errors;
using BookSpace.Domain.Common;
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

        // WP-5: the general case FluentValidation's special-case below always
        // was — an AppException that needs to hand the client more than a
        // reason code (NoOccurrencesCreatedException's per-occurrence
        // breakdown). Empty for every exception that doesn't opt in, so this
        // changes nothing for the 20-odd existing subclasses.
        if (exception is AppException { Extensions.Count: > 0 } appExceptionWithExtensions)
        {
            foreach (var (key, value) in appExceptionWithExtensions.Extensions)
            {
                problemDetails.Extensions[key] = value;
            }
        }

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

    // Every Application-layer rejection arrives as the single AppException case,
    // mapped by its Kind — the extension point WP-2 left here, now filled in. A new
    // failure needs a reason code and a Kind, not a case in this switch: WP-4's
    // booking rejections (SlotUnavailable, CapacityExceeded, OutsideAvailability,
    // BlackoutPeriod, ResourceArchived, ApprovalRequired) will land here with no
    // further plumbing.
    //
    // The remaining cases are exceptions from outside the Application layer, which
    // carry no Kind to read: EF concurrency, FluentValidation, and the
    // tenant-isolation bug guard.
    private static (int StatusCode, string Title, string ReasonCode) Map(Exception exception) => exception switch
    {
        // The reason code always comes from the exception, never from here — only
        // the handler that threw it knows whether this was a bad credential, an
        // archived resource, or an overlapping window. FR-2.1 / FR-2.2, CLAUDE.md §6.
        AppException appException => (
            StatusFor(appException.Kind),
            TitleFor(appException.Kind),
            appException.ReasonCode),
        DbUpdateConcurrencyException => (
            StatusCodes.Status409Conflict,
            "The record was modified by another request.",
            "ConcurrencyConflict"),
        // CLAUDE.md §4.2. Always an application bug, never something a client
        // legitimately triggers — deliberately generic so the response never
        // hints at tenant boundaries; the reason code is for grepping logs.
        TenantIsolationViolationException => (
            StatusCodes.Status500InternalServerError,
            "An unexpected error occurred.",
            "TenantIsolationViolation"),
        FluentValidation.ValidationException => (
            StatusCodes.Status400BadRequest,
            "One or more validation errors occurred.",
            "ValidationFailed"),
        _ => (
            StatusCodes.Status500InternalServerError,
            "An unexpected error occurred.",
            "UnexpectedError"),
    };

    // The Application layer knows nothing about HTTP (CLAUDE.md §3), so the
    // Kind -> status translation lives here, once. 422 for RuleViolation rather
    // than a second 400: the request was well-formed and understood, and a rule
    // refused it — which is exactly what 422 means, and it lets a client tell
    // "you sent nonsense" apart from "the booking rules said no" without reading
    // the reason code. The cost is that 422 is less universally recognized than
    // 400; the reason code carries the real meaning either way.
    private static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.RuleViolation => StatusCodes.Status422UnprocessableEntity,
        // A Kind added to the enum without a mapping here: 500 rather than a
        // guess, because a wrong status code is a lie the client acts on.
        _ => StatusCodes.Status500InternalServerError,
    };

    // Deliberately generic per Kind, never per failure: the exception message is
    // for the log (AppException says why), and the reason code is what a client
    // branches on.
    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "The request contains an invalid value.",
        ErrorKind.Unauthorized => "Authentication failed.",
        ErrorKind.NotFound => "The requested resource was not found.",
        ErrorKind.Conflict => "The request conflicts with the current state.",
        ErrorKind.RuleViolation => "The request was rejected by a rule.",
        _ => "An unexpected error occurred.",
    };
}
