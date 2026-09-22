using BookSpace.Application.Abstractions;
using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Bookings;

// Who may see whose bookings (FR-4.4, decision 0002), stated once and shared by
// both read handlers — GET /bookings and GET /bookings/{id}.
//
// A static rules class with no injected dependencies, matching
// ResourceWriteRules and BlackoutPeriodRules: it is a pure function of the
// caller's identity and what they asked for, which is what makes it directly
// unit-testable without a database, a token or a request.
//
// **This is the whole of decision 0002's authorization half.** That record says
// the TenantAdmin check "belongs in the Application layer, not the Domain
// entity" — Booking.Cancel accepts any actor id and records it, deliberately
// without deciding who was allowed to call it. This file is where that decision
// is made, for reads in 2a and for the cancel in 2b.
internal static class BookingReadRules
{
    // Resolves the owner question — me, that member, anybody, or anybody on my
    // resources — from the caller and the query.
    //
    // Callers pass isTenantAdmin rather than an ICurrentUser so the rule stays a
    // pure function; the handler does the IsInRole calls. It is a required
    // parameter for the reason BookingOwnerFilter has no public constructor: a
    // defaulted "am I an admin" flag is the one mistake here that fails open.
    //
    // **approverResourceIds carries WP-5 Phase 3's widening** (decision 0018's
    // queue): non-null only when the handler has already resolved the caller as
    // an Approver requesting scope=tenant, in which case it is that Approver's
    // `FindApprovableResourceIdsAsync` result (possibly empty — assigned to
    // nothing is a legal, if uninteresting, answer). The handler does that one
    // repository call itself rather than this file doing it, keeping this class
    // the pure function every other Rules class in this codebase is; the
    // reasoning is BookingApprovalReach's, one door over. Null unconditionally
    // means "not that case", so a member's or a not-scope-tenant Approver's call
    // falls through to the same Owner(callerUserId) a plain member gets.
    //
    // The privilege check is **repeated** here even though the validator has
    // already refused a member (or a non-widened Approver) sending either
    // parameter. Two gates rather than one, on the same reasoning that has
    // capacity checked twice on the create path: the validator's job is to give
    // a client a 400 that names the field, and this one's is to be the gate that
    // is structurally impossible to route around — a future caller that
    // dispatches the query without the pipeline, or a validator someone forgets
    // to type against a new query record (CLAUDE.md §12's discovery gotcha),
    // still cannot widen a member's view.
    public static BookingOwnerFilter ResolveOwnerFilter(
        Guid callerUserId,
        bool isTenantAdmin,
        Guid? requestedUserId,
        BookingScope scope,
        IReadOnlyCollection<Guid>? approverResourceIds = null)
    {
        if (isTenantAdmin)
        {
            // userId narrows to one member; scope=tenant drops the restriction.
            // The validator refuses both together, so the order of these two
            // arms is not a precedence rule anyone has to remember.
            if (requestedUserId is { } userId)
            {
                return BookingOwnerFilter.Owner(userId);
            }

            return scope == BookingScope.Tenant
                ? BookingOwnerFilter.AnyOwner
                : BookingOwnerFilter.Owner(callerUserId);
        }

        // userId stays TenantAdmin-only regardless of approverResourceIds — an
        // Approver's queue is resource-restricted, not member-restricted, so
        // this branch never reads requestedUserId at all.
        if (approverResourceIds is not null && scope == BookingScope.Tenant)
        {
            return BookingOwnerFilter.AnyOwnerRestrictedToResources(approverResourceIds);
        }

        return BookingOwnerFilter.Owner(callerUserId);
    }

    // The same question for one booking by id, phrased as the filter the
    // repository applies to its WHERE clause rather than as a comparison the
    // handler makes after loading.
    //
    // **Filtering in the query, not checking after the read**, and the
    // difference matters: a load-then-compare leaves a path where the DTO for
    // someone else's booking exists in memory and one early return away from the
    // wire. A booking the caller may not see simply does not come back, and the
    // handler's only branch is null → BookingNotFoundException, which is the 404
    // decision 0002 and BookingNotFoundException both require (never a 403,
    // which would confirm the booking exists).
    // **approverResourceIds widens it** (WP-7 Phase 6, decision 0027): non-null
    // only when the handler has already resolved the caller as a non-admin
    // holding Approver, in which case it is that Approver's
    // `FindApprovableResourceIdsAsync` result — possibly empty, which is a legal
    // answer meaning "assigned to nothing".
    //
    // The gap this closes was found by clicking, not by review (WP-7 Phase 6
    // step 3): an Approver could see a booking in the queue, and was allowed to
    // *decide* on it, but could not *open* it — the list half of this file was
    // widened in WP-5 Phase 3 and the detail half was not, so the link the queue
    // renders answered 404. Whether the two reads agree is not a detail: an
    // approver being asked to judge a request must be able to read the thing
    // they are judging.
    //
    // **The widening is a union, not a narrowing, and that is the whole
    // subtlety.** `OwnerOrResources`, never
    // `AnyOwnerRestrictedToResources` — an Approver may see a booking because it
    // is theirs *or* because it is on a resource they gate, and the two sets do
    // not contain each other. Handing this read the list's AND-shaped filter
    // would have silently taken away an Approver's ability to read their own
    // bookings on resources they do not approve for, which every plain member
    // can do. The seeded approver owns no bookings, so no amount of clicking the
    // dev data would have shown it.
    //
    // There is deliberately **no scope parameter here**. A detail read asks
    // about one booking, so there is no "how wide" question for a client to
    // answer and nothing for a validator to refuse — which is why an Approver's
    // reach applies unconditionally here while it needs `scope=tenant` above.
    public static BookingOwnerFilter ResolveDetailFilter(
        Guid callerUserId,
        bool isTenantAdmin,
        IReadOnlyCollection<Guid>? approverResourceIds = null)
    {
        if (isTenantAdmin)
        {
            return BookingOwnerFilter.AnyOwner;
        }

        // An empty set falls through rather than being passed on: "their own, or
        // one of no resources" is exactly "their own", and spelling it as the
        // plainer filter keeps the query one predicate shorter for the common
        // case of an Approver assigned to nothing.
        if (approverResourceIds is { Count: > 0 })
        {
            return BookingOwnerFilter.OwnerOrResources(callerUserId, approverResourceIds);
        }

        return BookingOwnerFilter.Owner(callerUserId);
    }

    // The one role that widens a booking read. Wrapped so both handlers ask the
    // same question of ICurrentUser and neither names the role itself.
    //
    // SysAdmin is deliberately absent, and would fail the TenantMember policy on
    // the controller long before reaching here: PRD §2 keeps the Platform
    // Operator out of tenant booking content, which decision 0012 enforces by
    // requiring the orgId claim a SysAdmin never has.
    public static bool CanSeeOtherMembersBookings(ICurrentUser currentUser) =>
        currentUser.IsInRole(Role.TenantAdmin);
}
