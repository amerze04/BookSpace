using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests.Bookings;

// Hand-written fake for the approval handlers (WP-5 Phase 3) — distinct from
// FakeBookingRepository (which stands in for dbo.CreateBooking's single call)
// and FakeSeriesBookingRepository (a queue of create outcomes): approve and
// reject each make one decision, so one settable outcome is enough here.
internal sealed class FakeApprovalBookingRepository : IBookingRepository
{
    private readonly BookingApprovalOutcome _outcome;

    public FakeApprovalBookingRepository(
        BookingApprovalResult result = BookingApprovalResult.Approved,
        int? remainingCapacity = null)
    {
        _outcome = new BookingApprovalOutcome(result, remainingCapacity);
    }

    // ---- Arrangement ----

    public Booking? Reachable { get; set; }

    public IReadOnlyList<Guid> ApprovableResourceIds { get; set; } = [];

    public ApprovalRequest? ExistingApprovalRequest { get; set; }

    // ---- Recorded calls ----

    public Guid? RequestedApprovalId { get; private set; }

    public ApprovalReach? RequestedReach { get; private set; }

    public Guid? ApprovedBookingId { get; private set; }

    public List<Notification> AddedNotifications { get; } = [];

    public int SaveChangesCount { get; private set; }

    public int ApproveAsyncCallCount { get; private set; }

    public Task<Booking?> FindForApprovalAsync(
        Guid bookingId, ApprovalReach reach, CancellationToken cancellationToken)
    {
        RequestedApprovalId = bookingId;
        RequestedReach = reach;

        return Task.FromResult(Reachable);
    }

    public Task<IReadOnlyList<Guid>> FindApprovableResourceIdsAsync(
        Guid approverUserId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovableResourceIds);

    public Task<ApprovalRequest?> FindApprovalRequestAsync(Guid bookingId, CancellationToken cancellationToken) =>
        Task.FromResult(ExistingApprovalRequest);

    public Task<BookingApprovalOutcome> ApproveAsync(
        Guid bookingId, Guid approverUserId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        ApproveAsyncCallCount++;
        ApprovedBookingId = bookingId;
        return Task.FromResult(_outcome);
    }

    public void AddNotifications(IEnumerable<Notification> notifications) =>
        AddedNotifications.AddRange(notifications);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        return Task.CompletedTask;
    }

    // ---- Unused by the approval handlers ----

    public Task<BookingCreationOutcome> CreateAsync(NewBooking booking, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PagedResult<ListBookingsQueryResponse>> ListAsync(
        ListBookingsQueryRequest query, BookingOwnerFilter owner, SortOption? sort,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<GetBookingQueryResponse?> FindDetailAsync(
        Guid bookingId, BookingOwnerFilter owner, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Booking?> FindForCancellationAsync(
        Guid bookingId, BookingOwnerFilter owner, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Booking>> FindOccurrencesToCancelAsync(
        Guid recurrenceRuleId, DateTime nowUtc, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void AddApprovalRequest(ApprovalRequest approvalRequest) => throw new NotSupportedException();

    public Task<int?> FindApprovalExpiryHoursAsync(Guid orgId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
