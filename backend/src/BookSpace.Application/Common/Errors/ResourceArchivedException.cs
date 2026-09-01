namespace BookSpace.Application.Common.Errors;

// ErrorKind.RuleViolation (FR-3.5). An archived resource stays readable and
// keeps its history, but refuses edits and new bookings — otherwise "archived"
// would mean nothing beyond a flag.
//
// Deliberately not thrown by the archive endpoint itself: archiving something
// already archived is the state the caller asked for, not a rule violation, so
// that path is idempotent. See ArchiveResourceCommandRequestHandler.
public sealed class ResourceArchivedException : AppException
{
    public ResourceArchivedException(Guid resourceId)
        : base(
            ErrorKind.RuleViolation,
            ReasonCodes.ResourceArchived,
            $"Resource {resourceId} is archived and cannot be edited.")
    {
    }
}
