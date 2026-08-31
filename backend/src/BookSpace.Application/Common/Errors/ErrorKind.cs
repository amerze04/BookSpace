namespace BookSpace.Application.Common.Errors;

// What *kind* of failure an AppException represents, in domain terms. The
// Application layer deliberately does not name HTTP status codes — that
// translation is GlobalExceptionHandler's job in BookSpace.Api, which is the
// only project that knows about HTTP at all (CLAUDE.md §3).
//
// Five kinds, each with a real caller. Resist adding a sixth without one:
// every kind is a new row in the status-code mapping and a new thing a client
// has to understand.
public enum ErrorKind
{
    // Input the shape validators can't judge: a syntactically fine value that
    // isn't acceptable (an unrecognized IANA timezone id, for instance).
    // FluentValidation failures do not come through here — they keep their own
    // ValidationException, because they carry per-field errors this doesn't.
    Validation,

    // The entity doesn't exist, or exists in another tenant — which, after
    // CLAUDE.md §4.2's filters, is indistinguishable from here and must stay
    // that way. Never report a cross-tenant id as anything but "not found".
    NotFound,

    // The request contradicts the current state of the data: a duplicate, or
    // something another actor already changed.
    Conflict,

    // Well-formed, unambiguous, and refused by a rule (§6 tier 4): a resource
    // is archived, a blackout covers the slot, approvers are required.
    RuleViolation,

    // Credentials or tokens. Kept in this enum rather than special-cased so
    // authentication maps through the same mechanism as everything else.
    Unauthorized,
}
