namespace BookSpace.Application.Features.Authentication;

// Mapped to 401 by GlobalExceptionHandler, which reads ReasonCode onto the
// ProblemDetails. The message is for the server log; the reason code is what
// the client gets.
public sealed class AuthenticationException : Exception
{
    public AuthenticationException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
