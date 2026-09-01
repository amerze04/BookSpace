namespace BookSpace.Application.Common.Errors;

// The base of every Application-layer rejection — a request refused for a
// reason the client should see a code for (CLAUDE.md §6: "rejections return a
// machine-readable reason code, not just a message").
//
// **Abstract, with a protected constructor**, on the mentor's advice
// (2026-09-01): each distinct failure is a named subclass that fixes its own
// Kind and ReasonCode, so the two cannot be paired wrongly. The original design
// took both as parameters, which meant `NotFound` alongside `ResourceArchived`
// compiled cleanly and shipped a 404 for something that should be a 422 — the
// catalogue documented the correct pairing in a comment, and comments do not
// compile. See the amendment section of
// docs/decisions/0016-error-contract-and-reason-codes.md.
//
// What that deliberately does NOT change: GlobalExceptionHandler still has one
// arm for this whole hierarchy and maps Kind -> status code once. A new failure
// needs a subclass and a catalogue entry, never a case in that switch — so
// WP-4's booking rejections still land with no API-layer plumbing.
//
// Message is for the server log only. It is never written to the response:
// nothing here decides what is safe to tell a client, so it assumes nothing is
// — the reason code is the contract, and a per-error client-facing detail can
// be added later without changing this shape. AuthenticationException depends
// on that (its message distinguishes "no such account" from "wrong password",
// which the client must never learn).
public abstract class AppException : Exception
{
    protected AppException(ErrorKind kind, string reasonCode, string message)
        : base(message)
    {
        Kind = kind;
        ReasonCode = reasonCode;
    }

    public ErrorKind Kind { get; }

    // A stable, machine-readable code from the catalogue, not free text — the
    // client branches on this, so changing one is a breaking API change.
    // Subclasses pass a ReasonCodes constant; they never inline a literal.
    public string ReasonCode { get; }
}
