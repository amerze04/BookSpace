using BookSpace.Application.Features.Bookings;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;

namespace BookSpace.UnitTests.Bookings;

// WP-4 Phase 2a. Decision 0002's authorization half, which BookingReadRules is
// the whole of: who may see whose bookings.
//
// It is a pure function of the caller and the query, so these tests need no
// database, no token and no request — which is the reason the rule was extracted
// rather than written inline in each of the two handlers.
public class BookingReadRulesTests
{
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly Guid Colleague = Guid.NewGuid();

    // ---- A plain member sees exactly themselves ----------------------------

    [Fact]
    public void AMemberSeesOnlyTheirOwnBookings()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: false, requestedUserId: null, BookingScope.Own);

        Assert.Equal(Caller, owner.UserId);
    }

    // The validator refuses these two before a handler runs, so this is the
    // second gate BookingReadRules documents: even asked directly, the rule
    // cannot be talked into widening a member's view. It is what makes the
    // privilege independent of the pipeline being wired correctly.
    [Fact]
    public void AMemberAskingForAColleagueStillSeesOnlyThemselves()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: false, requestedUserId: Colleague, BookingScope.Own);

        Assert.Equal(Caller, owner.UserId);
    }

    [Fact]
    public void AMemberAskingForTheWholeTenantStillSeesOnlyThemselves()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: false, requestedUserId: null, BookingScope.Tenant);

        Assert.Equal(Caller, owner.UserId);
    }

    [Fact]
    public void AMemberSeesOnlyTheirOwnBookingById()
    {
        var owner = BookingReadRules.ResolveDetailFilter(Caller, isTenantAdmin: false);

        Assert.Equal(Caller, owner.UserId);
    }

    // ---- A TenantAdmin can widen, but only by asking ----------------------

    // The default is their own, not the tenant: a TenantAdmin is a member of
    // their tenant before they are its admin, so GET /bookings with no
    // parameters answers "what have *I* booked" for everyone.
    [Fact]
    public void AnAdminDefaultsToTheirOwnBookings()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: true, requestedUserId: null, BookingScope.Own);

        Assert.Equal(Caller, owner.UserId);
    }

    [Fact]
    public void AnAdminCanNarrowToOneMember()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: true, requestedUserId: Colleague, BookingScope.Own);

        Assert.Equal(Colleague, owner.UserId);
    }

    [Fact]
    public void AnAdminCanWidenToTheWholeTenant()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: true, requestedUserId: null, BookingScope.Tenant);

        Assert.Null(owner.UserId);
        Assert.Same(BookingOwnerFilter.AnyOwner, owner);
    }

    // Decision 0002: an admin reading one booking by id sees any in their
    // tenant, which is what makes the same route serve both actors.
    [Fact]
    public void AnAdminSeesAnyBookingById()
    {
        var owner = BookingReadRules.ResolveDetailFilter(Caller, isTenantAdmin: true);

        Assert.Null(owner.UserId);
    }

    // ---- The role that does the widening ----------------------------------

    [Theory]
    [InlineData(Role.Member, false)]
    [InlineData(Role.Approver, false)]
    [InlineData(Role.TenantAdmin, true)]
    public void OnlyTenantAdminCanSeeOtherMembersBookings(Role role, bool expected)
    {
        var currentUser = new FixedCurrentUser(Caller, role);

        Assert.Equal(expected, BookingReadRules.CanSeeOtherMembersBookings(currentUser));
    }

    // Approver sits between Member and TenantAdmin in AuthorizationPolicies, so
    // "not an admin" has to mean every non-admin — the same reasoning WP-3's
    // non-admin 403 tests use. Asserted separately from the theory above because
    // it is the one that would silently pass if the rule checked "any role".
    [Fact]
    public void AnApproverIsNotAnAdminForBookingReads()
    {
        var approver = new FixedCurrentUser(Caller, Role.Approver, Role.Member);

        Assert.False(BookingReadRules.CanSeeOtherMembersBookings(approver));
    }

    // No principal means no roles, so the least-privileged answer — a caller
    // with no identity can never be treated as an admin.
    [Fact]
    public void ACallerWithNoRolesIsNotAnAdmin()
    {
        Assert.False(BookingReadRules.CanSeeOtherMembersBookings(new FixedCurrentUser(Caller)));
    }

    // ---- The approver queue (WP-5 Phase 3, decision 0018) ------------------

    // approverResourceIds is what the handler passes only after resolving the
    // caller as an Approver requesting scope=tenant — see
    // ListBookingsQueryRequestHandler. Passed here directly, since the rule
    // itself does not re-derive the role.
    [Fact]
    public void AnApproverRequestingTenantScopeIsRestrictedToTheirResources()
    {
        var resourceId = Guid.NewGuid();

        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: false, requestedUserId: null, BookingScope.Tenant, [resourceId]);

        Assert.Null(owner.UserId);
        Assert.Equal([resourceId], owner.ResourceIds);
    }

    // Assigned to nothing is a legal answer, not an error — the restriction is
    // still applied, it is simply empty.
    [Fact]
    public void AnApproverAssignedToNoResourcesSeesNoTenantWideRows()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: false, requestedUserId: null, BookingScope.Tenant, []);

        Assert.Null(owner.UserId);
        Assert.Empty(owner.ResourceIds!);
    }

    // The restriction only applies to scope=tenant. An Approver's own-scope
    // read (the default) is untouched even if the handler somehow passed a
    // resource list anyway — this file's own defense-in-depth reasoning
    // applied to the new parameter.
    [Fact]
    public void ApproverResourceIdsAreIgnoredOutsideTenantScope()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: false, requestedUserId: null, BookingScope.Own, [Guid.NewGuid()]);

        Assert.Equal(Caller, owner.UserId);
        Assert.Null(owner.ResourceIds);
    }

    // A TenantAdmin's sweep is unrestricted regardless of what an
    // approverResourceIds argument carries — the admin branch is resolved
    // first and never reads it, on the same reasoning userId stays
    // TenantAdmin-only in the other direction.
    [Fact]
    public void AnAdminsTenantScopeIgnoresAnyApproverResourceRestriction()
    {
        var owner = BookingReadRules.ResolveOwnerFilter(
            Caller, isTenantAdmin: true, requestedUserId: null, BookingScope.Tenant, [Guid.NewGuid()]);

        Assert.Null(owner.UserId);
        Assert.Null(owner.ResourceIds);
        Assert.Same(BookingOwnerFilter.AnyOwner, owner);
    }

    // ---- BookingOwnerFilter itself ----------------------------------------

    // AnyOwner has to be asked for by name. This is the property that makes the
    // type worth having over a bare Guid?: there is no argument to omit and no
    // constructor to pass null to, so the fail-open state is unreachable by
    // accident (see BookingOwnerFilter).
    [Fact]
    public void AnOwnerFilterCarriesTheUserItWasGiven()
    {
        Assert.Equal(Colleague, BookingOwnerFilter.Owner(Colleague).UserId);
        Assert.Null(BookingOwnerFilter.AnyOwner.UserId);
        Assert.Null(BookingOwnerFilter.Owner(Colleague).ResourceIds);
        Assert.Null(BookingOwnerFilter.AnyOwner.ResourceIds);
    }

    [Fact]
    public void AnyOwnerRestrictedToResourcesCarriesNoOwnerButTheResourcesItWasGiven()
    {
        var resourceId = Guid.NewGuid();
        var filter = BookingOwnerFilter.AnyOwnerRestrictedToResources([resourceId]);

        Assert.Null(filter.UserId);
        Assert.Equal([resourceId], filter.ResourceIds);
    }
}
