namespace BookSpace.Application.Features.Bookings;

// Which owner's bookings a read may return, resolved once by BookingReadRules
// and then handed to the repository (WP-4 Phase 2a).
//
// **Why a type instead of a `Guid?`.** The repository needs "this user" or
// "anyone in the tenant", and a bare nullable id spells the second as null —
// so a handler that forgot to set it, or a new caller that defaulted the
// argument, would silently widen a member's list to the whole tenant. That is
// the fail-open shape CLAUDE.md §4.2 objects to, one scope down: tenant
// isolation still holds, but *member* isolation would not.
//
// With named factories there is no way to get AnyOwner by omission — it has to
// be asked for by name, and the one place that asks is the rule that has just
// checked the caller is a TenantAdmin. It is also the reason there is no public
// constructor: `new BookingOwnerFilter(null)` would put the fail-open state
// back within reach.
//
// It is not a substitute for the tenant filter. Every booking query still goes
// through the tenant-filtered DbSet, so AnyOwner means "any owner *in this
// tenant*" and never reaches another org's rows (§4.2, AC-4).
// **WP-5 Phase 3 added ResourceIds** for the approver queue (decision 0018):
// an Approver's `scope=tenant` reach is "any owner, but only the resources I
// approve for" — a restriction orthogonal to UserId, so it is a second field
// rather than a variant of the owner question. Null means unrestricted, same
// convention as UserId, for the same reason: an Approver with an empty list
// (assigned to nothing) still has to be distinguishable from one with no
// restriction at all, and a bare empty collection can't carry that.
public sealed record BookingOwnerFilter
{
    private BookingOwnerFilter(Guid? userId, IReadOnlyCollection<Guid>? resourceIds)
    {
        UserId = userId;
        ResourceIds = resourceIds;
    }

    // Null means no owner restriction. Read by the repository, which applies it
    // as a WHERE clause; never interpreted anywhere else.
    public Guid? UserId { get; }

    // Null means no resource restriction. Set only for an Approver's
    // scope=tenant read — a TenantAdmin's AnyOwner carries no restriction here,
    // matching ApprovalReach's own AnyResource/ForResources split for the same
    // two roles.
    public IReadOnlyCollection<Guid>? ResourceIds { get; }

    public static BookingOwnerFilter Owner(Guid userId) => new(userId, null);

    // The cast is required, not decorative: a record's compiler-generated copy
    // constructor makes a bare `new(null)` ambiguous with it.
    public static BookingOwnerFilter AnyOwner { get; } = new((Guid?)null, null);

    // An Approver's widened read: no owner restriction, but only the resources
    // this caller is assigned to approve.
    public static BookingOwnerFilter AnyOwnerRestrictedToResources(IReadOnlyCollection<Guid> resourceIds) =>
        new((Guid?)null, resourceIds);
}
