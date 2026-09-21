using System.Data;
using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Bookings;
using BookSpace.Application.Features.Bookings.GetBooking;
using BookSpace.Application.Features.Bookings.ListBookings;
using BookSpace.Domain.Entities;
using BookSpace.Domain.Enums;
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

            try
            {
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

                // Hardening pass, P1: the status the procedure actually stored,
                // which can differ from booking.Status when RequiresApproval was
                // true under the lock — see BookingCreationOutcome.ActualStatus.
                var actualStatus = reader.IsDBNull(2) ? (BookingStatus?)null : ParseStatus(reader.GetString(2));

                return new BookingCreationOutcome(Parse(resultCode), remainingCapacity, actualStatus);
            }
            catch (SqlException ex) when (IsPrimaryKeyViolation(ex))
            {
                // Hardening pass, P1/2 — the ambiguous-commit case.
                // IUnitOfWork's execution strategy retries this whole delegate
                // on a transient failure, and booking.Id is minted once by the
                // caller specifically so a retry re-inserts the *same* row
                // rather than a second one (IUnitOfWork's own header). That
                // means a retry can only ever hit PK_Bookings if a *previous*
                // attempt's INSERT already committed on the server before
                // this client learned the outcome — a dropped connection
                // after commit, not a genuine duplicate. Rather than let that
                // constraint violation surface as an unhandled 500 for an
                // operation that, from the caller's perspective, already
                // succeeded, read the row back and report the same Created
                // outcome the first attempt would have. See
                // ReadBackAlreadyCreatedAsync for the one case this
                // deliberately still throws: the row exists but does not
                // match what this call actually asked for.
                //
                // Bug fix (found while verifying the hardening pass, not part
                // of it): dbo.CreateBooking runs under SET XACT_ABORT ON, so a
                // runtime error there — this PK violation included — makes SQL
                // Server roll back the *entire* ambient transaction itself,
                // regardless of who opened it. command.Transaction (the .NET
                // SqlTransaction IUnitOfWork began) is therefore already dead
                // by the time control returns here; handing it to a new
                // command throws "An error occurred using a transaction"
                // instead of reading anything back, which — since reports
                // still had nothing Created — went on to make
                // CreateRecurrenceSeriesCommandRequestHandler try to delete a
                // RecurrenceRule that earlier, already-committed occurrences
                // still reference, surfacing as an FK-constraint 500
                // (RecurrenceSeriesIdempotencyEndpointTests caught this). The
                // read-back below deliberately runs with no transaction at
                // all — the row it is reading was committed by a *previous*,
                // already-completed attempt, so an ordinary autocommit read is
                // both correct and all that is left usable on this
                // connection. UnitOfWork.ExecuteAsync has the other half: it
                // must not then try to commit a transaction the server has
                // already ended.
                return await ReadBackAlreadyCreatedAsync(connection, booking, cancellationToken);
            }
        }
        finally
        {
            if (openedHere)
            {
                await _context.Database.CloseConnectionAsync();
            }
        }
    }

    // Hardening pass, P1/2. Error 2627 is a unique-constraint/PK violation —
    // checked by number, not message text, since SQL Server's message is
    // locale-dependent. dbo.CreateBooking's only unique key is PK_Bookings, so
    // there is nothing else this number could mean on this specific INSERT.
    private static bool IsPrimaryKeyViolation(SqlException ex) => ex.Number == 2627;

    // Reads back the row a PK violation on retry implies already exists, and
    // reports it as this call's own outcome — but only if it is genuinely the
    // *same* logical booking, not merely a Guid that happens to collide.
    // ResourceId/StartsAtUtc/EndsAtUtc/Quantity all matching is what makes
    // that distinction rather than assuming it: a real mismatch means
    // something worse than a retry is going on, and this deliberately does
    // not paper over that — it rethrows and lets the original 500 stand,
    // which is at least an honest signal that something needs investigating.
    private static async Task<BookingCreationOutcome> ReadBackAlreadyCreatedAsync(
        System.Data.Common.DbConnection connection,
        NewBooking booking,
        CancellationToken cancellationToken)
    {
        // No transaction set, deliberately — see the catch site's comment.
        // The ambient one is already dead (XACT_ABORT rolled it back on the
        // server the moment the PK violation happened), and the row this
        // reads was committed before that rollback even started, so a plain
        // autocommit read on the same connection sees it correctly.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ResourceId, StartsAtUtc, EndsAtUtc, Quantity, Status
            FROM dbo.Bookings WHERE Id = @BookingId;
            """;
        command.Parameters.Add(new SqlParameter("@BookingId", SqlDbType.UniqueIdentifier) { Value = booking.Id });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            // The PK violation said this row exists; it does not, a moment
            // later, in the same transaction. Something stranger than a retry
            // is happening — surface it rather than guess.
            throw new InvalidOperationException(
                $"dbo.CreateBooking reported a primary key violation for booking {booking.Id}, "
                + "but no row with that id could be read back.");
        }

        var matches = reader.GetGuid(0) == booking.ResourceId
            && reader.GetDateTime(1) == booking.StartsAtUtc
            && reader.GetDateTime(2) == booking.EndsAtUtc
            && reader.GetInt32(3) == booking.Quantity;

        if (!matches)
        {
            throw new InvalidOperationException(
                $"Booking {booking.Id} already exists but does not match the request that just tried to "
                + "create it — a genuine id collision, not a retry of the same operation.");
        }

        return new BookingCreationOutcome(
            BookingCreationResult.Created,
            RemainingCapacity: null,
            ParseStatus(reader.GetString(4)),
            WasAlreadyCreated: true);
    }

    // ---- The approval re-check (WP-5 Phase 3, decision 0023 inherited whole) ----
    //
    // Same shape as CreateAsync above, for the same reasons: raw ADO because
    // the procedure answers with two columns, opened through EF's own
    // connection so TenantSessionContextInterceptor still fires, and enlisted
    // in the ambient transaction so a business rejection here rolls back
    // together with whatever else IUnitOfWork's delegate staged.
    public async Task<BookingApprovalOutcome> ApproveAsync(
        Guid bookingId,
        Guid approverUserId,
        bool callerIsTenantAdmin,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;

        if (openedHere)
        {
            await _context.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "dbo.ApproveBooking";
            command.CommandType = CommandType.StoredProcedure;
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

            command.Parameters.Add(new SqlParameter("@BookingId", SqlDbType.UniqueIdentifier)
            { Value = bookingId });
            command.Parameters.Add(new SqlParameter("@ApproverUserId", SqlDbType.UniqueIdentifier)
            { Value = approverUserId });
            command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2)
            { Scale = 0, Value = nowUtc });
            command.Parameters.Add(new SqlParameter("@CallerIsTenantAdmin", SqlDbType.Bit)
            { Value = callerIsTenantAdmin });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("dbo.ApproveBooking returned no result row.");
            }

            var resultCode = reader.GetString(0);
            var remainingCapacity = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);

            return new BookingApprovalOutcome(ParseApproval(resultCode), remainingCapacity);
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

        bookings = ApplyOwnerFilter(bookings, owner);

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
                _context.Users.Where(u => u.Id == b.UserId).Select(u => u.FullName).First(),
                b.RecurrenceRuleId,
                b.StartsAtUtc,
                b.EndsAtUtc,
                b.Quantity,
                b.Title,
                b.Status,
                b.CreatedAtUtc))
            .ToPagedResultAsync(query, cancellationToken);
    }

    // The one place a BookingOwnerFilter becomes a WHERE clause, shared by the
    // list and the detail read (WP-7 Phase 6, decision 0027).
    //
    // **It is shared because the two used to disagree, and the disagreement was
    // invisible.** ListAsync applied both restrictions; FindDetailAsync applied
    // only UserId and silently ignored ResourceIds, which had been on the filter
    // since WP-5 Phase 3. Nothing handed the detail read a resource-restricted
    // filter, so nothing leaked — but the first caller to do so would have got
    // *any* booking in the tenant back, which is the fail-open shape
    // BookingOwnerFilter's own header exists to rule out. One helper means a
    // future field on the filter cannot be honoured by one read and dropped by
    // the other.
    //
    // The combinator is the filter's, not this method's: All for the list's
    // "any owner, on my resources", Any for the detail's "mine, or on my
    // resources". See BookingOwnerFilter for why those differ.
    //
    // Deliberately **not** used by FindForCancelAsync below, which applies only
    // the owner restriction: decision 0002 gives the cancel to the booking's
    // owner and to a TenantAdmin, and an Approver's resource reach does not
    // extend to cancelling other people's bookings. That method says so itself
    // rather than quietly passing a filter this one would widen.
    private static IQueryable<Booking> ApplyOwnerFilter(
        IQueryable<Booking> bookings,
        BookingOwnerFilter owner)
    {
        var ownerUserId = owner.UserId;
        var resourceIds = owner.ResourceIds;

        if (ownerUserId is { } userId && resourceIds is { } ids)
        {
            return owner.Combine == BookingOwnerFilter.Combination.Any
                ? bookings.Where(b => b.UserId == userId || ids.Contains(b.ResourceId))
                : bookings.Where(b => b.UserId == userId && ids.Contains(b.ResourceId));
        }

        if (ownerUserId is { } soleUserId)
        {
            return bookings.Where(b => b.UserId == soleUserId);
        }

        if (resourceIds is { } soleResourceIds)
        {
            return bookings.Where(b => soleResourceIds.Contains(b.ResourceId));
        }

        return bookings;
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

        bookings = ApplyOwnerFilter(bookings, owner);

        return bookings
            .Select(b => new GetBookingQueryResponse(
                b.Id,
                b.ResourceId,
                _context.Resources.Where(r => r.Id == b.ResourceId).Select(r => r.Name).First(),
                b.UserId,
                _context.Users.Where(u => u.Id == b.UserId).Select(u => u.FullName).First(),
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

        // Only the owner restriction, deliberately — this method does *not* go
        // through ApplyOwnerFilter (WP-7 Phase 6, decision 0027). Decision 0002
        // gives the cancel to the booking's owner and to a TenantAdmin, and an
        // Approver's resource reach widens what they may *read*, never what they
        // may cancel. Routing this through the shared helper would silently hand
        // an Approver the ability to cancel any booking on a resource they gate.
        if (owner.UserId is { } ownerUserId)
        {
            bookings = bookings.Where(b => b.UserId == ownerUserId);
        }

        return bookings.FirstOrDefaultAsync(cancellationToken);
    }

    // ---- The whole-series cancel (WP-5 Phase 2, FR-5.3) ----

    // Ordered so the response lists cancelled occurrences in a stable,
    // readable order rather than whatever order the engine returns — the same
    // reasoning IBlackoutPeriodRepository.FindBookingsToCancelAsync gives.
    public async Task<IReadOnlyList<Booking>> FindOccurrencesToCancelAsync(
        Guid recurrenceRuleId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await _context.Bookings
            .Where(b => b.RecurrenceRuleId == recurrenceRuleId
                && b.EndsAtUtc > nowUtc
                && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .OrderBy(b => b.StartsAtUtc)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);

    // ---- The approval reach and decision (WP-5 Phase 3) ----

    // Tracked: an approve does not mutate this entity through EF (the status
    // change goes through dbo.ApproveBooking), but a reject does, via
    // Booking.Reject. Loading it the same way for both keeps one method
    // serving both handlers rather than a tracked/untracked pair.
    public Task<Booking?> FindForApprovalAsync(
        Guid bookingId, ApprovalReach reach, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reach);

        var bookings = _context.Bookings.Where(b => b.Id == bookingId);

        if (reach.ResourceIds is { } resourceIds)
        {
            bookings = bookings.Where(b => resourceIds.Contains(b.ResourceId));
        }

        return bookings.FirstOrDefaultAsync(cancellationToken);
    }

    // Raw SQL against ResourceApprovers rather than a LINQ projection: it is an
    // EF *owned* collection (OwnsMany, ResourceConfiguration.cs) with no
    // queryable DbSet of its own and no ResourceId CLR property on
    // Resource.ApproverAssignment to project — the same shape
    // BookingRepository.CreateAsync's own header names as a reason to step
    // outside EF's normal query surface. Database.SqlQuery<T> rather than raw
    // ADO: this reads a single scalar column, which is exactly what it is for
    // (see CreateAsync's header for why the procedure calls need the heavier
    // tool instead).
    //
    // Goes through the tenant-filtered connection like every other query
    // here — ResourceApprovers carries no OrgId of its own, but its rows are
    // meaningless without their Resource, which RLS and the query filter
    // already scope to this tenant.
    public async Task<IReadOnlyList<Guid>> FindApprovableResourceIdsAsync(
        Guid approverUserId, CancellationToken cancellationToken) =>
        await _context.Database
            .SqlQuery<Guid>($"SELECT [ResourceId] FROM [ResourceApprovers] WHERE [UserId] = {approverUserId}")
            .ToListAsync(cancellationToken);

    // Tracked, since Decide() mutates it and the change has to be saved.
    // FirstOrDefaultAsync rather than Find(): a booking reachable through
    // FindForApprovalAsync's tenant-filtered query is already known visible,
    // but Find() would bypass that filter if this were ever called on its
    // own, and there is no reason to open that door.
    public Task<ApprovalRequest?> FindApprovalRequestAsync(Guid bookingId, CancellationToken cancellationToken) =>
        _context.ApprovalRequests.FirstOrDefaultAsync(a => a.BookingId == bookingId, cancellationToken);

    // Hardening pass, P2. Filtered to Pending in the query, not just by
    // convention: a booking that was Confirmed (never had a live request) or
    // whose request was already Approved/Rejected/Expired must not be
    // touched by a cancellation path calling Withdraw on this list.
    public async Task<IReadOnlyList<ApprovalRequest>> FindPendingApprovalRequestsAsync(
        IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken) =>
        await _context.ApprovalRequests
            .Where(a => bookingIds.Contains(a.BookingId) && a.Decision == ApprovalDecision.Pending)
            .ToListAsync(cancellationToken);

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

    // Hardening pass, P1. Against the known set, exactly like Parse/
    // ParseApproval above: an unrecognised value means the procedure and this
    // file have drifted, and that should fail loudly rather than silently map
    // to a guessed status.
    private static BookingStatus ParseStatus(string status) => status switch
    {
        "Pending" => BookingStatus.Pending,
        "Confirmed" => BookingStatus.Confirmed,
        _ => throw new InvalidOperationException(
            $"dbo.CreateBooking returned an unrecognised ActualStatus '{status}'."),
    };

    private static BookingApprovalResult ParseApproval(string resultCode) => resultCode switch
    {
        "Approved" => BookingApprovalResult.Approved,
        "BookingNotPending" => BookingApprovalResult.BookingNotPending,
        "ApproverNotEligible" => BookingApprovalResult.ApproverNotEligible,
        "ResourceNotFound" => BookingApprovalResult.ResourceNotFound,
        "ResourceArchived" => BookingApprovalResult.ResourceArchived,
        "BlackoutPeriod" => BookingApprovalResult.BlackoutPeriod,
        "SlotUnavailable" => BookingApprovalResult.SlotUnavailable,
        "CapacityExceeded" => BookingApprovalResult.CapacityExceeded,
        _ => throw new InvalidOperationException(
            $"dbo.ApproveBooking returned an unrecognised result code '{resultCode}'."),
    };
}
