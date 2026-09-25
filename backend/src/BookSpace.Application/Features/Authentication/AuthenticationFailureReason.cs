namespace BookSpace.Application.Features.Authentication;

// Machine-readable reason codes, in the spirit of CLAUDE.md §6. Deliberately
// coarse on the login side: every credential failure reports InvalidCredentials
// regardless of cause, so the endpoint cannot be used to enumerate accounts.
//
// These five stay here rather than in ReasonCodes (the catalogue for every
// other code) so that rationale sits beside the codes it constrains. Every
// code in either file is unique across both — ReasonCodesTests proves it.
public static class AuthenticationFailureReason
{
    public const string InvalidCredentials = "InvalidCredentials";
    public const string InvalidRefreshToken = "InvalidRefreshToken";
    public const string RefreshTokenExpired = "RefreshTokenExpired";
    public const string RefreshTokenReuseDetected = "RefreshTokenReuseDetected";
    public const string AccountInactive = "AccountInactive";

    // Account activation, and coarse for the same reason InvalidCredentials is.
    // Expired, already redeemed, never existed, and "the account was
    // deactivated between the invitation and the click" all report this one
    // code — a response that told them apart would make the endpoint an oracle
    // for which invitations are outstanding, which is the concern decision
    // `0018` collapsed three approver-ineligibility reasons into one code to
    // avoid. See docs/user-management-plan.md §4.2.
    public const string InvalidActivationToken = "InvalidActivationToken";
}
