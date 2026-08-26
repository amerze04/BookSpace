namespace BookSpace.Application.Features.Authentication;

// Machine-readable reason codes, in the spirit of CLAUDE.md §6. Deliberately
// coarse on the login side: every credential failure reports InvalidCredentials
// regardless of cause, so the endpoint cannot be used to enumerate accounts.
public static class AuthenticationFailureReason
{
    public const string InvalidCredentials = "InvalidCredentials";
    public const string InvalidRefreshToken = "InvalidRefreshToken";
    public const string RefreshTokenExpired = "RefreshTokenExpired";
    public const string RefreshTokenReuseDetected = "RefreshTokenReuseDetected";
    public const string AccountInactive = "AccountInactive";
}
