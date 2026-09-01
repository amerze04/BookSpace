namespace BookSpace.Application.Common.Errors;

// ErrorKind.Validation, not RuleViolation: the value is simply wrong, and the
// shape validator could not have judged it — it cannot know the host's timezone
// database (see ITimeZoneCatalog).
//
// Thrown for a Windows zone id ("Eastern Standard Time") as well as an
// unrecognized one, even though TimeZoneInfo resolves the former on Windows.
// CLAUDE.md §4.3 needs IANA specifically.
//
// Named ...IdException, not ...Exception, because System.InvalidTimeZoneException
// already exists — TimeZoneInfo throws it for a corrupt timezone database, which
// this codebase could plausibly see. Shadowing a BCL exception name is how a
// `catch` ends up catching something nobody meant. This is the one place the
// "<ReasonCode>Exception" naming convention bends; the reason code itself stays
// InvalidTimeZone, because the wire contract should not be reshaped by what the
// BCL happens to have named a type.
public sealed class InvalidTimeZoneIdException : AppException
{
    public InvalidTimeZoneIdException(string timeZoneId)
        : base(
            ErrorKind.Validation,
            ReasonCodes.InvalidTimeZone,
            $"'{timeZoneId}' is not a recognized IANA timezone id.")
    {
    }
}
