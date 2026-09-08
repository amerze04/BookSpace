using System.Data;
using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// The one production path that creates a booking (WP-4 Phase 1b, CLAUDE.md
// §4.1). It does exactly one thing: hand dbo.CreateBooking its arguments and
// translate the row it returns.
//
// **Raw ADO rather than ExecuteSqlAsync / FromSql**, which is a deviation from
// CLAUDE.md §5's usual instruction worth stating plainly. §5's rule is about
// parameterisation — "never ExecuteSqlRaw with string concatenation" — and every
// value below is a typed SqlParameter, so the rule's intent is met more strictly
// than interpolation meets it. The reason for the deviation is the return shape:
// the procedure answers with two columns, and EF's Database.SqlQuery<T> reads a
// single scalar column, so the alternatives were a keyless entity type in the
// model purely to describe a procedure's result, or output parameters bolted
// onto an interpolated EXEC. Both are more machinery around less clarity.
//
// **No IgnoreQueryFilters and no TenantBypassScope anywhere here**, and unlike
// the other repositories that is not merely good hygiene — it is the guarantee.
// The procedure depends on RLS being in force on this connection: it reads
// Resources through the filtered table precisely so that a connection with no
// tenant context fails closed instead of counting zero overlapping bookings and
// overbooking. Bypassing isolation here would defeat the check that makes that
// safe.
internal sealed class BookingRepository : IBookingRepository
{
    private readonly BookSpaceDbContext _context;

    public BookingRepository(BookSpaceDbContext context)
    {
        _context = context;
    }

    public async Task<BookingCreationOutcome> CreateAsync(
        NewBooking booking,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(booking);

        var connection = _context.Database.GetDbConnection();

        // Opened through EF, never through the raw DbConnection, and the
        // difference is load-bearing: TenantSessionContextInterceptor sets the
        // tenant session context on EF's ConnectionOpened event (CLAUDE.md §4.2,
        // mechanism 3). Calling connection.OpenAsync() directly would skip it,
        // and the procedure would then correctly refuse everything with
        // ResourceNotFound.
        //
        // Normally already open — the caller is inside IUnitOfWork's
        // transaction — so this is the out-of-transaction case only, and it
        // closes what it opened rather than leaving the context's connection in
        // a state it did not ask for.
        var openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "dbo.CreateBooking";
            command.CommandType = CommandType.StoredProcedure;

            // Enlists in the ambient transaction when there is one. Without this
            // the procedure would run on the same connection but outside the
            // transaction, and SQL Server would refuse it.
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

            AddParameters(command, booking);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // The procedure always returns exactly one row, on every path
            // including its refusals. No row means the text changed and this
            // translation did not — better to say so than to guess.
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "dbo.CreateBooking returned no result row.");
            }

            var resultCode = reader.GetString(0);
            var remainingCapacity = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);

            return new BookingCreationOutcome(Parse(resultCode), remainingCapacity);
        }
        finally
        {
            if (openedHere)
            {
                await _context.Database.CloseConnectionAsync();
            }
        }
    }

    // ---- The reads (WP-4 Phase 2a, FR-4.4) ----
    //
    // Plain EF projections, and CLAUDE.md §4.1 does not reach them: it governs
    // writes that add demand against Resources.Capacity, and a read adds none.
    // See IBookingRepository's header.
    //
    // **The owner filter is applied here as a WHERE clause**, from the
    // BookingOwnerFilter the handler resolved (decision 0002, BookingReadRules).
    // Nothing in this file decides who may see what — it applies the decision,
    // which is why AnyOwner reads as an absent predicate rather than as a
    // privilege being granted.
    //
    // Tenant isolation is untouched: both queries go through the tenant-filtered
    // DbSet, so the owner filter narrows *within* a tenant and can never reach
    // across one (§4.2, AC-4).

    public Task<PagedResult<ListBookingsQueryResponse>> ListAsync(
        ListBookingsQueryRequest query,
        BookingOwnerFilter owner,
        SortOption? sort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(owner);

        var bookings = _context.Bookings.AsNoTracking();

        if (owner.UserId is { } ownerUserId)
        {
            bookings = bookings.Where(b => b.UserId == ownerUserId);
        }

        // Overlap, not containment, exactly as the blackout list does it: a
        // booking that started before the window and runs into it is part of
        // what "next week" contains. Each bound is applied independently so
        // either can be omitted, and both omitted means the whole history
        // (owner's call, 2026-09-08).
        if (query.From is { } from)
        {
            bookings = bookings.Where(b => b.EndsAtUtc > from);
        }

        if (query.To is { } to)
        {
            bookings = bookings.Where(b => b.StartsAtUtc < to);
        }

        // Compared as an enum, not a string: the column stores names
        // (CLAUDE.md §5) and EF's value converter translates the comparison, so
        // no spelling ever enters the expression tree. A local rather than
        // query.Status inside the lambda, matching the overlap predicates
        // elsewhere in this project.
        if (query.Status is { } status)
        {
            bookings = bookings.Where(b => b.Status == status);
        }

        // No 404 when the resource id is unknown, deliberately unlike the
        // blackout list: there the resource is in the *route*, so an unknown id
        // has to be distinguished from "this resource has no blackouts". Here it
        // is one filter among five on a collection that belongs to the member,
        // and "no bookings match" is the honest answer to every combination of
        // them. It also avoids leaking that a resource id exists.
        if (query.ResourceId is { } resourceId)
        {
            bookings = bookings.Where(b => b.ResourceId == resourceId);
        }

        // Projection after ordering, so ToPagedResultAsync still sees the
        // OrderBy in the expression tree and COUNT(*) runs over the filtered set
        // rather than a materialized list.
        //
        // **The resource name comes from a correlated subquery, not a join.**
        // Booking has no navigation property (BookingConfiguration configures
        // every relationship with HasOne<T>().WithMany() and no navigation), and
        // a Join here would sit between the OrderBy and the projection, where EF
        // can push the ordering into a subquery that SQL Server is then free to
        // ignore. A scalar subquery keeps the OrderBy at the top level, which is
        // the shape ToPagedResultAsync's contract depends on.
        //
        // First(), not FirstOrDefault(), and it cannot throw: Bookings.ResourceId
        // is a real FK, and FK_Bookings_Resources_SameOrg forces the two rows to
        // share an OrgId (decision 0006), so a booking visible through the
        // tenant filter always has a resource visible through it too. Archived
        // resources are deliberately not excluded — FR-3.5 keeps them readable,
        // and filtering them here would erase a member's own booking history.
        return ApplyOrder(bookings, sort)
            .Select(b => new ListBookingsQueryResponse(
                b.Id,
                b.ResourceId,
                _context.Resources.Where(r => r.Id == b.ResourceId).Select(r => r.Name).First(),
                b.UserId,
                b.StartsAtUtc,
                b.EndsAtUtc,
                b.Quantity,
                b.Title,
                b.Status))
            .ToPagedResultAsync(query, cancellationToken);
    }

    // Null means "not visible to this caller", which folds three cases into one
    // — no such id, another tenant's id (the query filter), another member's
    // booking (the owner filter) — so the handler can answer with a single
    // indistinguishable BookingNotFound (AC-4).
    //
    // FirstOrDefaultAsync, never DbSet.Find(): Find can return a tracked entity
    // without querying at all, which would skip the query filter (CLAUDE.md
    // §4.2) — and here it would skip the owner filter too, which is the more
    // immediate hazard since the write path tracks the booking it just created.
    //
    // AsNoTracking because nothing here mutates. Phase 2b's cancel needs a
    // tracked booking and gets its own loader, exactly as the resource reads and
    // FindForUpdateAsync are kept separate.
    public Task<GetBookingQueryResponse?> FindDetailAsync(
        Guid bookingId,
        BookingOwnerFilter owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var bookings = _context.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId);

        if (owner.UserId is { } ownerUserId)
        {
            bookings = bookings.Where(b => b.UserId == ownerUserId);
        }

        return bookings
            .Select(b => new GetBookingQueryResponse(
                b.Id,
                b.ResourceId,
                _context.Resources.Where(r => r.Id == b.ResourceId).Select(r => r.Name).First(),
                b.UserId,
                b.RecurrenceRuleId,
                b.StartsAtUtc,
                b.EndsAtUtc,
                b.Quantity,
                b.Title,
                b.Status,
                b.CheckedInAtUtc,
                b.CancelledByUserId,
                b.CancelledAtUtc,
                b.CancellationReason,
                b.CreatedAtUtc,
                b.UpdatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // ---- The cancel (WP-4 Phase 2b, FR-4.4) ----

    // Tracked, unlike the two reads above: the caller mutates the entity through
    // Booking.Cancel and the change has to be saved. No AsNoTracking, and no
    // projection — the same split IResourceRepository keeps between its reads
    // and FindForUpdateAsync.
    //
    // The owner filter is in the WHERE clause for a stronger reason here than on
    // the reads: a booking loaded and then refused would be a booking the caller
    // could have had cancelled. Filtered out, it is simply not there, and the
    // handler's only branch is null → BookingNotFound (AC-4).
    //
    // FirstOrDefaultAsync, never DbSet.Find(): Find can return a tracked entity
    // without querying, which would skip both the query filter (CLAUDE.md §4.2)
    // and the owner filter — and on this path the create handler may well have
    // the booking tracked already.
    public Task<Booking?> FindForCancellationAsync(
        Guid bookingId,
        BookingOwnerFilter owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var bookings = _context.Bookings.Where(b => b.Id == bookingId);

        if (owner.UserId is { } ownerUserId)
        {
            bookings = bookings.Where(b => b.UserId == ownerUserId);
        }

        return bookings.FirstOrDefaultAsync(cancellationToken);
    }

    // ---- The rows derived from a booking (WP-4 Phase 1c) ----
    //
    // Plain EF adds. They carry no capacity claim, so none of the procedure's
    // machinery applies; they commit with the booking because the handler runs
    // both inside IUnitOfWork.

    public void AddApprovalRequest(ApprovalRequest approvalRequest) =>
        _context.ApprovalRequests.Add(approvalRequest);

    public void AddNotifications(IEnumerable<Notification> notifications) =>
        _context.Notifications.AddRange(notifications);

    // Organizations carries no query filter and no RLS predicate — it is the
    // tenant table itself, not a tenant-owned one (CLAUDE.md §4.2 lists the five
    // that do). So the org id is supplied by the caller, taken from the resource
    // it already loaded through the filtered DbSet, which is what keeps this from
    // being a way to read another tenant's settings.
    public Task<int?> FindApprovalExpiryHoursAsync(Guid orgId, CancellationToken cancellationToken) =>
        _context.Organizations
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.ApprovalExpiryHours)
            .FirstOrDefaultAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        _context.SaveChangesAsync(cancellationToken);

    // Maps a canonical whitelist name (BookingSortFields) onto a typed OrderBy —
    // the Infrastructure half of the split described on IResourceRepository: the
    // sort string never enters the expression.
    //
    // The default arm covers both "no sort supplied" and sort=startsAtUtc, which
    // is this endpoint's own default order: a booking list is read as a schedule,
    // so chronological needs no explanation.
    //
    // **Ordering by Status orders by the stored *name*, not the enum's
    // declaration order**, because the column is a string (CLAUDE.md §5). So
    // ascending gives Cancelled, Completed, Confirmed, NoShow, Pending, Rejected
    // rather than the lifecycle order the enum declares. That is worth knowing
    // and not worth fixing: the point of the sort is to *group* statuses so an
    // admin can find the Pending ones together, and alphabetical is stable and
    // predictable, whereas ordering by lifecycle would need a CASE expression
    // that changes meaning every time a status is added.
    //
    // Always finishes with ThenBy(Id). Offset paging over a non-unique order has
    // undefined boundaries among tied rows, and ties are the norm here rather
    // than the exception — every booking of a pooled resource for the same slot
    // shares a StartsAtUtc, and a whole page could share a Status.
    // ToPagedResultAsync can only detect a *missing* order, not a non-unique one
    // (docs/decisions/0015).
    private static IOrderedQueryable<Booking> ApplyOrder(IQueryable<Booking> source, SortOption? sort)
    {
        var descending = sort?.Descending ?? false;

        IOrderedQueryable<Booking> ordered = sort?.Field switch
        {
            BookingSortFields.CreatedAtUtc => descending
                ? source.OrderByDescending(b => b.CreatedAtUtc)
                : source.OrderBy(b => b.CreatedAtUtc),
            BookingSortFields.Status => descending
                ? source.OrderByDescending(b => b.Status)
                : source.OrderBy(b => b.Status),
            _ => descending
                ? source.OrderByDescending(b => b.StartsAtUtc)
                : source.OrderBy(b => b.StartsAtUtc),
        };

        return ordered.ThenBy(b => b.Id);
    }

    private static void AddParameters(System.Data.Common.DbCommand command, NewBooking booking)
    {
        // Explicit SqlDbType on the instants, not inferred. datetime2(0) rounds
        // on write (CLAUDE.md §4.3), and letting the driver pick datetime2(7)
        // for a parameter compared against a datetime2(0) column is how a
        // boundary comparison ends up off by a fraction of a second.
        command.Parameters.Add(new SqlParameter("@BookingId", SqlDbType.UniqueIdentifier)
        { Value = booking.Id });
        command.Parameters.Add(new SqlParameter("@ResourceId", SqlDbType.UniqueIdentifier)
        { Value = booking.ResourceId });
        command.Parameters.Add(new SqlParameter("@UserId", SqlDbType.UniqueIdentifier)
        { Value = booking.UserId });
        command.Parameters.Add(new SqlParameter("@StartsAtUtc", SqlDbType.DateTime2)
        { Scale = 0, Value = booking.StartsAtUtc });
        command.Parameters.Add(new SqlParameter("@EndsAtUtc", SqlDbType.DateTime2)
        { Scale = 0, Value = booking.EndsAtUtc });
        command.Parameters.Add(new SqlParameter("@Quantity", SqlDbType.Int)
        { Value = booking.Quantity });

        // The enum's name, never its int — CLAUDE.md §5, and the column is
        // constrained by CK_Bookings_Status to the same six strings.
        command.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 20)
        { Value = booking.Status.ToString() });

        command.Parameters.Add(new SqlParameter("@CreatedByUserId", SqlDbType.UniqueIdentifier)
        { Value = booking.CreatedByUserId });
        command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2)
        { Scale = 0, Value = booking.NowUtc });
        command.Parameters.Add(new SqlParameter("@RecurrenceRuleId", SqlDbType.UniqueIdentifier)
        { Value = (object?)booking.RecurrenceRuleId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Title", SqlDbType.NVarChar, 200)
        { Value = (object?)booking.Title ?? DBNull.Value });
    }

    // Parsed against the known set rather than Enum.Parse over whatever arrives:
    // an unrecognised code means the procedure and this file have drifted, and
    // silently mapping it to something plausible would turn a deployment mistake
    // into a wrong answer about capacity.
    private static BookingCreationResult Parse(string resultCode) => resultCode switch
    {
        "Created" => BookingCreationResult.Created,
        "ResourceNotFound" => BookingCreationResult.ResourceNotFound,
        "ResourceArchived" => BookingCreationResult.ResourceArchived,
        "BlackoutPeriod" => BookingCreationResult.BlackoutPeriod,
        "SlotUnavailable" => BookingCreationResult.SlotUnavailable,
        "CapacityExceeded" => BookingCreationResult.CapacityExceeded,
        _ => throw new InvalidOperationException(
            $"dbo.CreateBooking returned an unrecognised result code '{resultCode}'."),
    };
}
