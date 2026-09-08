using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Availability;
using BookSpace.Domain.Entities;

namespace BookSpace.UnitTests.Bookings;

// Hand-written fakes, matching the rest of the suite — no mocking library
// anywhere in this codebase.

internal sealed class FakeAvailabilityRepository : IAvailabilityRepository
{
    private readonly Resource? _resource;

    public FakeAvailabilityRepository(
        Resource? resource = null,
        IReadOnlyList<UtcInterval>? blackouts = null,
        IReadOnlyList<BookedQuantity>? bookings = null)
    {
        _resource = resource;
        Blackouts = blackouts ?? [];
        Bookings = bookings ?? [];
    }

    public IReadOnlyList<UtcInterval> Blackouts { get; }

    public IReadOnlyList<BookedQuantity> Bookings { get; }

    // The span each read was asked for, so a test can assert the handler scopes
    // its queries to the requested interval rather than to something wider.
    public UtcInterval? BlackoutSpan { get; private set; }

    public UtcInterval? BookingSpan { get; private set; }

    public Task<Resource?> FindWithScheduleAsync(Guid resourceId, CancellationToken cancellationToken) =>
        Task.FromResult(_resource is not null && _resource.Id == resourceId ? _resource : null);

    public Task<IReadOnlyList<UtcInterval>> FindBlackoutIntervalsAsync(
        Guid resourceId,
        UtcInterval span,
        CancellationToken cancellationToken)
    {
        BlackoutSpan = span;
        return Task.FromResult(Blackouts);
    }

    public Task<IReadOnlyList<BookedQuantity>> FindBookedQuantitiesAsync(
        Guid resourceId,
        UtcInterval span,
        CancellationToken cancellationToken)
    {
        BookingSpan = span;
        return Task.FromResult(Bookings);
    }
}

// Stands in for dbo.CreateBooking. It cannot simulate the lock — nothing in a
// unit test can, which is why the concurrency proof lives in
// CreateBookingProcedureTests against a real SQL Server. What it can do is let a
// handler test drive every result code the procedure is allowed to return.
internal sealed class FakeBookingRepository : IBookingRepository
{
    private readonly BookingCreationOutcome _outcome;

    public FakeBookingRepository(
        BookingCreationResult result = BookingCreationResult.Created,
        int? remainingCapacity = null,
        int? approvalExpiryHours = null)
    {
        _outcome = new BookingCreationOutcome(result, remainingCapacity);
        ApprovalExpiryHours = approvalExpiryHours;
    }

    public int? ApprovalExpiryHours { get; }

    public NewBooking? Created { get; private set; }

    public ApprovalRequest? AddedApprovalRequest { get; private set; }

    public List<Notification> AddedNotifications { get; } = new();

    public int SaveChangesCount { get; private set; }

    // Records the order the handler did things in, so a test can assert that
    // nothing was saved after a rejection rather than merely that the call threw.
    public bool SavedAfterCreate { get; private set; }

    public Task<BookingCreationOutcome> CreateAsync(NewBooking booking, CancellationToken cancellationToken)
    {
        Created = booking;
        return Task.FromResult(_outcome);
    }

    public void AddApprovalRequest(ApprovalRequest approvalRequest) =>
        AddedApprovalRequest = approvalRequest;

    public void AddNotifications(IEnumerable<Notification> notifications) =>
        AddedNotifications.AddRange(notifications);

    public Task<int?> FindApprovalExpiryHoursAsync(Guid orgId, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalExpiryHours);

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCount++;
        SavedAfterCreate = Created is not null;
        return Task.CompletedTask;
    }

    // ---- The reads (Phase 2a) ----
    //
    // These record the arguments rather than simulating a query, because what a
    // read *handler* is responsible for is the owner filter it resolves — the
    // filtering itself is a WHERE clause, which only a database can exercise
    // (BookingReadEndpointTests does). So the fake's job is to let a test assert
    // exactly which BookingOwnerFilter the handler decided on.

    public ListBookingsQueryRequest? ListedQuery { get; private set; }

    public BookingOwnerFilter? ListedOwner { get; private set; }

    public SortOption? ListedSort { get; private set; }

    public Guid? RequestedDetailId { get; private set; }

    public BookingOwnerFilter? DetailOwner { get; private set; }

    // What FindDetailAsync hands back. Null is the "not visible to this caller"
    // answer the handler has to turn into BookingNotFound.
    public GetBookingQueryResponse? Detail { get; set; }

    public Task<PagedResult<ListBookingsQueryResponse>> ListAsync(
        ListBookingsQueryRequest query,
        BookingOwnerFilter owner,
        SortOption? sort,
        CancellationToken cancellationToken)
    {
        ListedQuery = query;
        ListedOwner = owner;
        ListedSort = sort;

        return Task.FromResult(
            PagedResult<ListBookingsQueryResponse>.Empty(query.Page, query.PageSize));
    }

    public Task<GetBookingQueryResponse?> FindDetailAsync(
        Guid bookingId,
        BookingOwnerFilter owner,
        CancellationToken cancellationToken)
    {
        RequestedDetailId = bookingId;
        DetailOwner = owner;

        return Task.FromResult(Detail);
    }

    // ---- The cancel (Phase 2b) ----

    // The tracked booking the cancel handler will mutate. A real Booking rather
    // than a DTO, because the handler calls Cancel on it and a test then asserts
    // the transition actually landed on the entity.
    public Booking? Cancellable { get; set; }

    public Guid? RequestedCancellationId { get; private set; }

    public BookingOwnerFilter? CancellationOwner { get; private set; }

    public Task<Booking?> FindForCancellationAsync(
        Guid bookingId,
        BookingOwnerFilter owner,
        CancellationToken cancellationToken)
    {
        RequestedCancellationId = bookingId;
        CancellationOwner = owner;

        return Task.FromResult(Cancellable);
    }
}

// Runs the delegate straight through. The real implementation's job — a
// transaction inside a retrying execution strategy — is EF behaviour that only a
// database can exercise; what a handler test needs from it is that the delegate
// runs once and its result comes back.
internal sealed class PassThroughUnitOfWork : IUnitOfWork
{
    public int Executions { get; private set; }

    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Executions++;
        return work(cancellationToken);
    }
}
