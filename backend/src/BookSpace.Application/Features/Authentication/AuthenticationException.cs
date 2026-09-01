using BookSpace.Application.Common.Errors;

namespace BookSpace.Application.Features.Authentication;

// Mapped to 401 by GlobalExceptionHandler, which reads ReasonCode onto the
// ProblemDetails. The message is for the server log; the reason code is what
// the client gets.
//
// An AppException with Kind = Unauthorized as of WP-3 Phase 1, so it maps
// through the same mechanism as every other rejection instead of its own switch
// case. Kept as a named type rather than replaced by a bare
// `new AppException(ErrorKind.Unauthorized, …)`: the authentication handlers
// throw it in a dozen places, and the type name is what makes
// "every credential failure looks identical to the client" (FR-2.1) greppable.
public sealed class AuthenticationException : AppException
{
    public AuthenticationException(string reasonCode, string message)
        : base(ErrorKind.Unauthorized, reasonCode, message)
    {
    }
}
