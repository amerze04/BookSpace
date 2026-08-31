namespace BookSpace.Application.Common.Errors;

// The one exception type an Application handler throws to reject a request for
// a reason the client should see a code for (CLAUDE.md §6: "rejections return a
// machine-readable reason code, not just a message").
//
// Filling in the extension point WP-2 deliberately left in
// GlobalExceptionHandler.Map: rather than a new exception type and a new switch
// case per failure, a handler throws this with a Kind and a ReasonCode, and the
// API maps Kind -> status code once. WP-3's failures and WP-4's booking
// rejection codes (SlotUnavailable, CapacityExceeded, …) both land here with no
// further plumbing. See docs/decisions/0016-error-contract-and-reason-codes.md.
//
// Message is for the server log only. It is never written to the response:
// nothing here decides what is safe to tell a client, so it assumes nothing is
// — the reason code is the contract, and a per-error client-facing detail can
// be added later without changing this shape. AuthenticationException depends
// on that (its message distinguishes "no such account" from "wrong password",
// which the client must never learn).
public class AppException : Exception
{
    public AppException(ErrorKind kind, string reasonCode, string message)
        : base(message)
    {
        Kind = kind;
        ReasonCode = reasonCode;
    }

    public ErrorKind Kind { get; }

    // A stable, machine-readable code from the catalogue, not free text — the
    // client branches on this, so changing one is a breaking API change.
    public string ReasonCode { get; }
}
