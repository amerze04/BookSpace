using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Resources.ReplaceAvailabilityWindows;

// FR-3.2, TenantAdmin only. PUT /resources/{id}/availability-windows.
//
// Replace-the-set rather than per-row POST/DELETE, which the domain asked for
// before the API did: AvailabilityWindow carries no audit columns precisely
// because entries are "typically bulk-replaced as a weekly set rather than
// individually edited". An endpoint shaped the other way would have made that
// comment wrong.
//
// It also makes the operation idempotent — sending the same schedule twice
// leaves the same schedule — which per-row endpoints cannot be, and which
// matters for a payload an admin is likely to keep in a script.
//
// An empty Windows list is legal and means "this resource opens at no time":
// see the validator for why that is not treated as a mistake.
public sealed record ReplaceAvailabilityWindowsCommandRequest(
    Guid ResourceId,
    IReadOnlyList<AvailabilityWindowCommandItem> Windows)
    : IRequest<ReplaceAvailabilityWindowsCommandResponse>;
