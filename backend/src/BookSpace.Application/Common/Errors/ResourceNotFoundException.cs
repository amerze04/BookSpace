namespace BookSpace.Application.Common.Errors;

// ErrorKind.NotFound (FR-3.1). Also the answer for another tenant's real
// resource id: after CLAUDE.md §4.2's query filter and RLS the two are
// indistinguishable from a handler, and must stay that way (AC-4). Nothing in
// the response says which case it was — the id in the message below never
// leaves the log.
public sealed class ResourceNotFoundException : AppException
{
    public ResourceNotFoundException(Guid resourceId)
        : base(
            ErrorKind.NotFound,
            ReasonCodes.ResourceNotFound,
            $"Resource {resourceId} was not found in the current tenant.")
    {
    }
}
