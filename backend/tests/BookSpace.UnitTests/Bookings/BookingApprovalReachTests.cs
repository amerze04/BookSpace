using BookSpace.Application.Features.Bookings;
using BookSpace.Domain.Enums;
using BookSpace.UnitTests.Resources;

namespace BookSpace.UnitTests.Bookings;

// WP-5 Phase 3, decision 0018 reapplied. Which of the two branches
// BookingApprovalReach.ResolveAsync takes, and what it asks the repository
// for when it needs to.
public class BookingApprovalReachTests
{
    private static readonly Guid ActorId = Guid.NewGuid();

    [Fact]
    public async Task ATenantAdminGetsAnyResourceWithoutQueryingApprovableResources()
    {
        var bookings = new FakeApprovalBookingRepository { ApprovableResourceIds = [Guid.NewGuid()] };

        var reach = await BookingApprovalReach.ResolveAsync(
            bookings, new FixedCurrentUser(ActorId, Role.TenantAdmin), ActorId, CancellationToken.None);

        Assert.Null(reach.ResourceIds);
    }

    [Fact]
    public async Task AnApproverGetsExactlyTheResourcesTheRepositoryReturns()
    {
        var resourceIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var bookings = new FakeApprovalBookingRepository { ApprovableResourceIds = resourceIds };

        var reach = await BookingApprovalReach.ResolveAsync(
            bookings, new FixedCurrentUser(ActorId, Role.Approver), ActorId, CancellationToken.None);

        Assert.Equal(resourceIds, reach.ResourceIds);
    }

    // Neither role — matches nothing, on the same "no 403, just unreachable"
    // shape BookingNotFoundException relies on elsewhere.
    [Fact]
    public async Task APlainMemberGetsNoResourcesAtAll()
    {
        var bookings = new FakeApprovalBookingRepository();

        var reach = await BookingApprovalReach.ResolveAsync(
            bookings, new FixedCurrentUser(ActorId, Role.Member), ActorId, CancellationToken.None);

        Assert.NotNull(reach.ResourceIds);
        Assert.Empty(reach.ResourceIds);
    }

    // A TenantAdmin who also holds Approver still gets the sweeping reach —
    // TenantAdmin is checked first, and its reach is a superset.
    [Fact]
    public async Task ATenantAdminWhoIsAlsoAnApproverStillGetsAnyResource()
    {
        var bookings = new FakeApprovalBookingRepository { ApprovableResourceIds = [Guid.NewGuid()] };

        var reach = await BookingApprovalReach.ResolveAsync(
            bookings, new FixedCurrentUser(ActorId, Role.TenantAdmin, Role.Approver), ActorId, CancellationToken.None);

        Assert.Null(reach.ResourceIds);
    }
}
