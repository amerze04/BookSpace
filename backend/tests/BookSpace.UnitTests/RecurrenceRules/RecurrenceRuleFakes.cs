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

// WP-5 Phase 1b/2. A plain recorder — IRecurrenceRuleRepository has nothing to
// simulate, since it embodies no locking protocol.
internal sealed class FakeRecurrenceRuleRepository : IRecurrenceRuleRepository
{
    public RecurrenceRule? Added { get; private set; }

    public RecurrenceRule? Removed { get; private set; }

    public int SaveChangesCount { get; private set; }

    public void Add(RecurrenceRule rule) => Added = rule;

    public void Remove(RecurrenceRule rule) => Removed = rule;

    // ---- Idempotent creation (hardening pass, item 11) ---------------------

    // Set by a test to simulate a prior attempt against this key — Creating
    // (resume), Active (replay) or absent (first attempt / a Failed retry,
    // which the test simulates by simply leaving this null).
    public RecurrenceCreationOperation? ExistingOperation { get; set; }

    // What FindByIdAsync hands back when GetOrCreateRuleAsync resolves an
    // existing operation to a rule it needs to reload.
    public RecurrenceRule? ExistingRule { get; set; }

    public RecurrenceCreationOperation? AddedOperation { get; private set; }

    public RecurrenceCreationOperation? RemovedOperation { get; private set; }

    // Bug fix, item 11's own found gap: false simulates two concurrent
    // first-time requests racing UQ_RecurrenceCreationOperations_Org_User_Key
    // and this attempt losing — a test sets this alongside ExistingOperation/
    // ExistingRule to describe what the "winner" it should resume looks like.
    // True (the default) is every test written before this fix, where the
    // insert always succeeds.
    public bool SaveNewOperationSucceeds { get; set; } = true;

    private int _findOperationCallCount;

    public Task<RecurrenceCreationOperation?> FindOperationAsync(
        Guid orgId, Guid userId, string idempotencyKey, CancellationToken cancellationToken)
    {
        _findOperationCallCount++;

        // A lost-race test sets ExistingOperation to the *winner's* row but
        // needs the *first* lookup (before this attempt tries its own insert)
        // to still see "nothing yet" — otherwise GetOrCreateRuleAsync would
        // resume the winner immediately and never reach TrySaveNewOperationAsync
        // at all, which is exactly the code path this is for testing.
        if (!SaveNewOperationSucceeds && _findOperationCallCount == 1)
        {
            return Task.FromResult<RecurrenceCreationOperation?>(null);
        }

        return Task.FromResult(ExistingOperation);
    }

    public void AddOperation(RecurrenceCreationOperation operation) => AddedOperation = operation;

    public void RemoveOperation(RecurrenceCreationOperation operation) => RemovedOperation = operation;

    public Task<RecurrenceRule?> FindByIdAsync(Guid recurrenceRuleId, CancellationToken cancellationToken) =>
        Task.FromResult(ExistingRule);

    public Task<bool> TrySaveNewOperationAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        return Task.FromResult(SaveNewOperationSucceeds);
    }

    // ---- The cancel (WP-5 Phase 2) ----

    // What FindForCancellationAsync hands back — null is the "not visible to
    // this caller" answer the handler turns into RecurrenceRuleNotFound.
    public RecurrenceRule? Cancellable { get; set; }

    public Guid? RequestedCancellationId { get; private set; }

    public BookingOwnerFilter? CancellationOwner { get; private set; }

    public Task<RecurrenceRule?> FindForCancellationAsync(
        Guid recurrenceRuleId, BookingOwnerFilter owner, CancellationToken cancellationToken)
    {
        RequestedCancellationId = recurrenceRuleId;
        CancellationOwner = owner;

        return Task.FromResult(Cancellable);
    }

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

    // Hardening pass, P2: simulates the genuinely-unexpected exception the
    // handler's try/catch (orphan prevention around the occurrence loop) is
    // for — a real DB error, not one of dbo.CreateBooking's own clean
    // rejection outcomes, which never throw. Null (the default) means every
    // call succeeds or is dequeued normally, matching every test written
    // before this pass. ThrowOnCallNumber is 1-based and 0 means "never" —
    // set both to make the Nth call to CreateAsync throw instead of
    // returning an outcome.
    public Exception? ThrowOnCreate { get; set; }

    public int ThrowOnCallNumber { get; set; }

    private int _createCallCount;

    public Task<BookingCreationOutcome> CreateAsync(NewBooking booking, CancellationToken cancellationToken)
    {
        Attempted.Add(booking);
        _createCallCount++;

        if (ThrowOnCreate is { } exception && _createCallCount == ThrowOnCallNumber)
        {
            throw exception;
        }

        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : _default;

        // Hardening pass, P1: a queued/default outcome that did not specify
        // ActualStatus (every one built before this pass) echoes the
        // requested status back, matching FakeBookingRepository's default —
        // "no downgrade" unless a test explicitly queues one.
        if (outcome.Result == BookingCreationResult.Created && outcome.ActualStatus is null)
        {
            outcome = outcome with { ActualStatus = booking.Status };
        }

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

    // ---- The whole-series cancel (WP-5 Phase 2) ----

    // Settable rather than recorded-from-a-call: the cancel handler's tests
    // arrange "these are the occurrences still cancellable" directly, the
    // same shape FakeBookingRepository.Cancellable uses for the single-booking
    // cancel.
    public List<Booking> CancellableOccurrences { get; set; } = [];

    public Guid? RequestedRecurrenceRuleId { get; private set; }

    public DateTime? RequestedNowUtc { get; private set; }

    public Task<IReadOnlyList<Booking>> FindOccurrencesToCancelAsync(
        Guid recurrenceRuleId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        RequestedRecurrenceRuleId = recurrenceRuleId;
        RequestedNowUtc = nowUtc;

        return Task.FromResult<IReadOnlyList<Booking>>(CancellableOccurrences);
    }

    // Hardening pass, P2. Empty by default — "nothing Pending to withdraw" —
    // which is what every existing series-cancel test here wants; a test
    // proving the withdraw behavior sets it explicitly.
    public IReadOnlyList<ApprovalRequest> PendingApprovalRequests { get; set; } = [];

    public Task<IReadOnlyList<ApprovalRequest>> FindPendingApprovalRequestsAsync(
        IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApprovalRequest>>(
            PendingApprovalRequests.Where(a => bookingIds.Contains(a.BookingId)).ToList());

    // ---- Unused by either RecurrenceRules handler ----

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

    public Task<BookingApprovalOutcome> ApproveAsync(
        Guid bookingId, Guid approverUserId, bool callerIsTenantAdmin, DateTime nowUtc,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Booking?> FindForApprovalAsync(
        Guid bookingId, ApprovalReach reach, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> FindApprovableResourceIdsAsync(
        Guid approverUserId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ApprovalRequest?> FindApprovalRequestAsync(Guid bookingId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
