using BookSpace.Application.Abstractions;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings;

// Which bookings an actor may decide on (WP-5 Phase 3, FR-7.1-7.5) — the
// approval counterpart to BookingOwnerFilter, and deliberately a separate
// type rather than a reuse of it: decision 0002's reach narrows by *owner*,
// this one narrows by *resource* (decision 0018's approver-eligibility set),
// which is not a question BookingOwnerFilter's shape can express.
//
// No public constructor for the same reason BookingOwnerFilter has none: the
// unrestricted case has to be asked for by name, so a handler that forgot to
// resolve a caller's actual role cannot silently widen to "any resource" by
// omission (CLAUDE.md §4.2's fail-open objection, one scope down again).
public sealed record ApprovalReach
{
    private ApprovalReach(IReadOnlyCollection<Guid>? resourceIds) => ResourceIds = resourceIds;

    // Null means no resource restriction — a TenantAdmin's sweeping reach,
    // decision 0002 extended to approvals. Non-null names exactly the
    // resources an Approver may decide on (decision 0018); read by the
    // repository as an additional WHERE ResourceId IN (...), alongside the
    // tenant filter every query already goes through.
    public IReadOnlyCollection<Guid>? ResourceIds { get; }

    public static ApprovalReach AnyResource { get; } = new((IReadOnlyCollection<Guid>?)null);

    public static ApprovalReach ForResources(IReadOnlyCollection<Guid> resourceIds) => new(resourceIds);
}

// Resolves an actor's ApprovalReach — shared by approve and reject, since
// decision 0018's answer to "who may decide" does not depend on which
// decision is being made (wp5-plan.md §5.3: "same reach as approve").
//
// Not a "Rules" class in this codebase's usual sense (BookingReadRules,
// ResourceWriteRules): those are pure functions with no injected
// dependencies, kept that way so they are unit-testable without a database.
// This one genuinely needs a query — which resources this caller approves
// for (decision 0018) is not knowable from the token alone — so it is named
// and shaped differently on purpose, taking the repository as a parameter
// rather than pretending to be pure.
internal static class BookingApprovalReach
{
    public static async Task<ApprovalReach> ResolveAsync(
        IBookingRepository bookings,
        ICurrentUser currentUser,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        // Decision 0002's sweeping reach, extended to approvals: a TenantAdmin
        // may decide on any Pending booking in their tenant.
        if (currentUser.IsInRole(Role.TenantAdmin))
        {
            return ApprovalReach.AnyResource;
        }

        // Decision 0018: an Approver may decide only on a booking whose
        // resource lists them. A plain Member matches neither branch and
        // falls through to an empty resource set below — reachable by
        // nothing, the same "no 403, just an empty answer" shape
        // BookingNotFoundException already relies on elsewhere.
        if (currentUser.IsInRole(Role.Approver))
        {
            var resourceIds = await bookings.FindApprovableResourceIdsAsync(actorUserId, cancellationToken);
            return ApprovalReach.ForResources(resourceIds);
        }

        return ApprovalReach.ForResources([]);
    }
}
