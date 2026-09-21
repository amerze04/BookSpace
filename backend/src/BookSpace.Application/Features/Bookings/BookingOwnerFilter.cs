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
//
// **WP-7 Phase 6 added Combine** (decision 0027), and it exists because the two
// reads genuinely need the two restrictions joined differently:
//
//   - The **list** joins them with AND. An Approver asking `scope=tenant` is
//     asking "what is booked on the resources I gate", and their own booking on
//     some other resource is not part of that question — they have `scope=own`
//     for that, which is the endpoint's default.
//   - The **detail** joins them with OR. There is no scope parameter on a read
//     of one booking, so a single filter has to answer for both reasons an
//     Approver may see it: it is theirs, *or* it is on a resource they gate.
//     An AND there would have been a regression — it would have taken away an
//     Approver's ability to read their own bookings on resources they do not
//     approve for, which every member has.
//
// Two restrictions and one combinator, rather than two types: the repository
// applies both through one shared helper (ApplyOwnerFilter), so the list and
// the detail cannot drift apart in how they read the same filter.
public sealed record BookingOwnerFilter
{
    // How the two restrictions below combine when both are set. Named rather
    // than boolean so a call site reads as a decision; irrelevant when fewer
    // than two restrictions are present, which is every case but the Approver's
    // detail read.
    public enum Combination
    {
        // Both must hold — this owner AND one of these resources.
        All,

        // Either suffices — this owner OR one of these resources.
        Any,
    }

    private BookingOwnerFilter(
        Guid? userId,
        IReadOnlyCollection<Guid>? resourceIds,
        Combination combine)
    {
        UserId = userId;
        ResourceIds = resourceIds;
        Combine = combine;
    }

    // Null means no owner restriction. Read by the repository, which applies it
    // as a WHERE clause; never interpreted anywhere else.
    public Guid? UserId { get; }

    // Null means no resource restriction. Set for an Approver's scope=tenant
    // list read and for an Approver's detail read — a TenantAdmin's AnyOwner
    // carries no restriction here, matching ApprovalReach's own
    // AnyResource/ForResources split for the same two roles.
    public IReadOnlyCollection<Guid>? ResourceIds { get; }

    public Combination Combine { get; }

    public static BookingOwnerFilter Owner(Guid userId) => new(userId, null, Combination.All);

    // The cast is required, not decorative: a record's compiler-generated copy
    // constructor makes a bare `new(null)` ambiguous with it.
    public static BookingOwnerFilter AnyOwner { get; } =
        new((Guid?)null, null, Combination.All);

    // An Approver's widened *list* read: no owner restriction, but only the
    // resources this caller is assigned to approve.
    public static BookingOwnerFilter AnyOwnerRestrictedToResources(
        IReadOnlyCollection<Guid> resourceIds) =>
        new((Guid?)null, resourceIds, Combination.All);

    // An Approver's widened *detail* read (decision 0027): their own booking, or
    // any booking on a resource they gate. The OR is the whole point — see the
    // header — and it is why this is its own factory rather than a flag a caller
    // could forget to set.
    public static BookingOwnerFilter OwnerOrResources(
        Guid userId,
        IReadOnlyCollection<Guid> resourceIds) =>
        new(userId, resourceIds, Combination.Any);
}
