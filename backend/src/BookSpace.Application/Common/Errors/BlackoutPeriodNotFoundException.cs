namespace BookSpace.Application.Common.Errors;

// ErrorKind.NotFound (FR-3.4). No such blackout on the resource in the path.
//
// One code for three cases, deliberately: the blackout id exists nowhere, it
// belongs to another tenant, or it exists in this tenant but hangs off a
// different resource than the route names. The first two are indistinguishable
// after CLAUDE.md §4.2's filters and must stay that way (AC-4); the third is
// folded in with them because reporting it separately would confirm that the id
// exists, which is the same disclosure.
//
// Distinct from ResourceNotFoundException even though both are 404s, because
// they say different things about the same URL: the resource is missing, or the
// resource is fine and the blackout is not. An admin debugging a script needs to
// know which half of the path is wrong.
public sealed class BlackoutPeriodNotFoundException : AppException
{
    public BlackoutPeriodNotFoundException(Guid blackoutPeriodId, Guid resourceId)
        : base(
            ErrorKind.NotFound,
            ReasonCodes.BlackoutPeriodNotFound,
            $"Blackout period {blackoutPeriodId} was not found on resource {resourceId} in the current tenant.")
    {
    }
}
