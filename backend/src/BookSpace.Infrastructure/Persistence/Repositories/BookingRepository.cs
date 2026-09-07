using System.Data;
using BookSpace.Application.Abstractions;
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
