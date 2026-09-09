using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests.RecurrenceRules;

// Hand-written fakes, matching the rest of the suite — no mocking library
// anywhere in this codebase. Distinct from Bookings.FakeBookingRepository
// (BookingFakes.cs), which answers with one fixed outcome for its single
// dbo.CreateBooking call: a series calls it once per occurrence, so this one
// answers from a queue and records every attempt rather than just the last.

// WP-5 Phase 1b. A plain recorder — IRecurrenceRuleRepository has nothing to
// simulate, since it embodies no locking protocol.
internal sealed class FakeRecurrenceRuleRepository : IRecurrenceRuleRepository
{
    public RecurrenceRule? Added { get; private set; }

    public int SaveChangesCount { get; private set; }

    public void Add(RecurrenceRule rule) => Added = rule;

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        return Task.CompletedTask;
    }
}

// Stands in for dbo.CreateBooking across a whole series. Outcomes are
// answered from a queue, in call order, falling back to a fixed default once
// exhausted — which is what lets a test drive "the third occurrence loses a
// race" without having to enumerate every occurrence before and after it.
internal sealed class FakeSeriesBookingRepository : IBookingRepository
{
    private readonly Queue<BookingCreationOutcome> _outcomes;
    private readonly BookingCreationOutcome _default;

    public FakeSeriesBookingRepository(
        IEnumerable<BookingCreationOutcome>? outcomes = null,
        BookingCreationResult defaultResult = BookingCreationResult.Created,
        int? approvalExpiryHours = null)
    {
        _outcomes = new Queue<BookingCreationOutcome>(outcomes ?? []);
        _default = new BookingCreationOutcome(defaultResult, null);
        ApprovalExpiryHours = approvalExpiryHours;
    }

    public int? ApprovalExpiryHours { get; }

    // Every attempted booking, whichever way it was decided — the report the
    // handler builds is asserted against the response, this list is for
    // asserting what the handler actually *sent* the procedure.
    public List<NewBooking> Attempted { get; } = [];

    public List<ApprovalRequest> AddedApprovalRequests { get; } = [];

    public List<Notification> AddedNotifications { get; } = [];

    public int SaveChangesCount { get; private set; }

    public Task<BookingCreationOutcome> CreateAsync(NewBooking booking, CancellationToken cancellationToken)
    {
        Attempted.Add(booking);
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : _default;
        return Task.FromResult(outcome);
    }

    public void AddApprovalRequest(ApprovalRequest approvalRequest) =>
        AddedApprovalRequests.Add(approvalRequest);

    public void AddNotifications(IEnumerable<Notification> notifications) =>
        AddedNotifications.AddRange(notifications);

    public Task<int?> FindApprovalExpiryHoursAsync(Guid orgId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalExpiryHours);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        return Task.CompletedTask;
    }

    // ---- Unused by CreateRecurrenceSeriesCommandRequestHandler ----

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
}
