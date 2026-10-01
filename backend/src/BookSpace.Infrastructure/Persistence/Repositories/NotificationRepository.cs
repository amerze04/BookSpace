using System.Data;
using BookSpace.Application.Abstractions;
using BookSpace.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BookSpace.Infrastructure.Persistence.Repositories;

// WP-8 Phase 2 (docs/wp8-plan.md). The dispatch job's port: claim due rows,
// compose an email for one, record what happened.
internal sealed class NotificationRepository : INotificationRepository
{
    private readonly BookSpaceDbContext _context;
    private readonly ITimeZoneCatalog _timeZones;

    public NotificationRepository(BookSpaceDbContext context, ITimeZoneCatalog timeZones)
    {
        _context = context;
        _timeZones = timeZones;
    }

    // **Raw ADO, not ExecuteSqlInterpolatedAsync**, for the return shape: this
    // needs several OUTPUT columns per claimed row, and EF's
    // ExecuteSql*Async only ever answers a rows-affected count (the shape
    // JobLeaseRepository's single-row compare-and-swap is content with).
    // Every value below is still a typed SqlParameter — CLAUDE.md §5's rule
    // is about parameterisation, not about which of EF's two raw-SQL entry
    // points is used.
    //
    // Hardening pass, finding 6: `UPDATE TOP (n)` alone has no defined row
    // order, so a sustained backlog had no guarantee the *oldest* overdue
    // row would ever be claimed ahead of a newer one. The CTE below adds a
    // real `ORDER BY` inside its own `TOP (n)`, and the UPDATE still runs
    // against that CTE in one atomic statement — selecting and claiming are
    // not two round trips a second claimer could race between.
    //
    // Hardening pass, finding 2: the EXISTS clause fences the claim to the
    // caller's own *current* lease ownership, checked against dbo.JobLeases
    // with the database's own clock (SYSUTCDATETIME(), matching
    // JobLeaseRepository's own authority, finding 1) rather than trusting
    // that PeriodicJobRunner's heartbeat has already noticed a lost lease.
    // See INotificationRepository's own header for why this job specifically
    // needs it and the other two do not.
    public async Task<IReadOnlyList<ClaimedNotification>> ClaimDueAsync(
        int batchSize, int maxAttempts, TimeSpan backoffBase, DateTime nowUtc,
        string jobName, Guid ownerId, CancellationToken cancellationToken)
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
            command.CommandText =
                """
                ;WITH Candidates AS (
                    SELECT TOP (@BatchSize) *
                    FROM dbo.Notifications WITH (UPDLOCK, READPAST)
                    WHERE SentAtUtc IS NULL
                      AND SendAtUtc <= @NowUtc
                      AND Attempts < @MaxAttempts
                      AND (Attempts = 0
                           OR DATEADD(SECOND, CAST(POWER(2, Attempts) * @BackoffBaseSeconds AS BIGINT), UpdatedAtUtc) <= @NowUtc)
                      AND EXISTS (
                          SELECT 1 FROM dbo.JobLeases
                          WHERE JobName = @JobName
                            AND OwnerId = @OwnerId
                            AND LeaseExpiresAtUtc > SYSUTCDATETIME())
                    ORDER BY SendAtUtc, CreatedAtUtc, Id
                )
                UPDATE Candidates
                SET Attempts = Attempts + 1,
                    UpdatedAtUtc = @NowUtc
                OUTPUT inserted.Id, inserted.BookingId, inserted.RecurrenceRuleId,
                       inserted.OccurrenceDate, inserted.RecipientUserId, inserted.Kind,
                       inserted.Attempts
                """;
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

            command.Parameters.Add(new SqlParameter("@BatchSize", SqlDbType.Int) { Value = batchSize });
            command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
            command.Parameters.Add(new SqlParameter("@MaxAttempts", SqlDbType.Int) { Value = maxAttempts });
            command.Parameters.Add(new SqlParameter("@BackoffBaseSeconds", SqlDbType.Int)
            {
                Value = (int)backoffBase.TotalSeconds,
            });
            command.Parameters.Add(new SqlParameter("@JobName", SqlDbType.NVarChar, 100) { Value = jobName });
            command.Parameters.Add(new SqlParameter("@OwnerId", SqlDbType.UniqueIdentifier) { Value = ownerId });

            var claimed = new List<ClaimedNotification>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(new ClaimedNotification(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : DateOnly.FromDateTime(reader.GetDateTime(3)),
                    reader.GetGuid(4),
                    Enum.Parse<NotificationKind>(reader.GetString(5)),
                    reader.GetInt32(6)));
            }

            return claimed;
        }
        finally
        {
            if (openedHere)
            {
                await _context.Database.CloseConnectionAsync();
            }
        }
    }

    // **Every read below runs inside TenantBypassScope with IgnoreQueryFilters.**
    // A claimed row can name a Booking/RecurrenceRule/User in *any* tenant —
    // one dispatch run walks due work across the whole platform in one tick —
    // and there is no ICurrentTenant to set from, because there is no request.
    // CLAUDE.md §4.2's own text ("only AuthenticationUserRepository may call
    // Enter()") is amended by this file, per docs/wp8-plan.md decision D9: a
    // job repository is now a second, equally-named exception, for the same
    // reason the first one is — nothing here has any other way to be told
    // which tenant a row belongs to.
    public async Task<EmailMessage?> BuildEmailAsync(
        ClaimedNotification notification, DateTime nowUtc, CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        var recipient = await _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Id == notification.RecipientUserId)
            .Select(u => new { u.Email, u.FullName })
            .FirstAsync(cancellationToken);

        var to = new EmailAddress(recipient.Email, recipient.FullName);
        // Hardening pass, finding 4: keyed on the notification row's own id,
        // so a repeated send of the *same* row — an ordinary retry, or the
        // rarer post-send-crash case — carries an identical Message-ID
        // rather than a fresh random one. See EmailMessage's own header for
        // what this can and cannot promise.
        var idempotencyKey = notification.Id.ToString("n");

        if (notification.BookingId is { } bookingId)
        {
            var booking = await _context.Bookings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(b => b.Id == bookingId)
                .Select(b => new { b.ResourceId, b.StartsAtUtc, b.EndsAtUtc, b.Status })
                .FirstAsync(cancellationToken);

            // Hardening pass, finding 5. A claimed row can sit unsent for a
            // while — the batch is bounded, the backlog is not — and the
            // booking it describes can move on in the meantime, sometimes
            // producing a second, *contradictory* row for the same booking
            // (Confirmed queued, then Cancelled before either is sent).
            // Suppressing every one of these here, at compose time, is what
            // actually closes that gap — ClaimDueAsync's own claim order
            // (finding 6) makes claiming fairer, but promises nothing about
            // *which* of two due rows for the same booking is sent first.
            //
            //   Confirmed — stale once the booking is no longer Confirmed.
            //   ApprovalRequested — stale once the booking is no longer
            //     Pending: already decided (Approved/Rejected/Expired) or
            //     cancelled before anyone acted.
            //   Reminder — stale once the booking is no longer Confirmed
            //     (decision D10, unchanged), *or* once its own interval has
            //     already ended: a reminder that arrives after the meeting
            //     is over is wrong under any reading, with no invented
            //     policy needed. Deliberately narrower than "already
            //     started": a reminder for a meeting *in progress* is still
            //     arguably useful, and drawing that line would be inventing
            //     a rule no FR asks for — the same trap the WP-7
            //     click-through found the booking detail screen in.
            //
            // Cancelled/Rejected/NoShowReleased/ApprovalExpired are
            // deliberately exempt: each is already a terminal fact about
            // something that already happened, and nothing here would make
            // it less true later.
            var isStale = notification.Kind switch
            {
                NotificationKind.Confirmed => booking.Status != BookingStatus.Confirmed,
                NotificationKind.ApprovalRequested => booking.Status != BookingStatus.Pending,
                NotificationKind.Reminder => booking.Status != BookingStatus.Confirmed || booking.EndsAtUtc <= nowUtc,
                _ => false,
            };

            if (isStale)
            {
                return null;
            }

            var resource = await _context.Resources
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(r => r.Id == booking.ResourceId)
                .Select(r => new { r.Name, r.TimeZoneId })
                .FirstAsync(cancellationToken);

            var zone = _timeZones.GetResourceTimeZone(resource.TimeZoneId);
            var when = FormatInterval(zone.ToLocal(booking.StartsAtUtc), zone.ToLocal(booking.EndsAtUtc), resource.TimeZoneId);
            var (subject, body) = ComposeForBooking(notification.Kind, resource.Name, when);

            return new EmailMessage(to, subject, body, IdempotencyKey: idempotencyKey);
        }

        var recurrenceRuleId = notification.RecurrenceRuleId
            ?? throw new InvalidOperationException(
                $"Notification {notification.Id} has neither a BookingId nor a RecurrenceRuleId.");

        var rule = await _context.RecurrenceRules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == recurrenceRuleId)
            .Select(r => new { r.ResourceId })
            .FirstAsync(cancellationToken);

        var ruleResource = await _context.Resources
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Id == rule.ResourceId)
            .Select(r => new { r.Name })
            .FirstAsync(cancellationToken);

        var (ruleSubject, ruleBody) = ComposeForRecurrenceRule(
            notification.Kind, ruleResource.Name, notification.OccurrenceDate);

        return new EmailMessage(to, ruleSubject, ruleBody, IdempotencyKey: idempotencyKey);
    }

    // Plain EF: Notifications carries no tenant filter and no RLS policy at
    // all (it is not one of §4.2's six tables), so this needs no bypass —
    // only Notification.MarkOutcome, the domain's own single source of truth
    // for what a resolved attempt looks like.
    public async Task RecordOutcomeAsync(
        Guid notificationId, DateTime nowUtc, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        var notification = await _context.Notifications
            .FirstAsync(n => n.Id == notificationId, cancellationToken);

        notification.MarkOutcome(nowUtc, succeeded, error);

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string FormatInterval(DateTime localStart, DateTime localEnd, string timeZoneId) =>
        $"{localStart:yyyy-MM-dd HH:mm}–{localEnd:HH:mm} ({timeZoneId})";

    // No design or copy requirements exist for these (no FR specifies wording)
    // — kept plain and factual, one arm per Kind this dispatch job can
    // actually see anchored to a Booking. The default arm throws rather than
    // guessing: a new Booking-anchored kind needs a line here before it can
    // ever be emailed (ApprovalExpired, WP-8 Phase 4, was exactly this case),
    // the same "one arm per reason, default throws" shape ReasonCodes' own
    // mapping switches use.
    private static (string Subject, string Body) ComposeForBooking(
        NotificationKind kind, string resourceName, string when) =>
        kind switch
        {
            NotificationKind.Confirmed => (
                $"Booking confirmed — {resourceName}",
                $"Your booking for {resourceName} on {when} is confirmed."),
            NotificationKind.Rejected => (
                $"Booking rejected — {resourceName}",
                $"Your request for {resourceName} on {when} was rejected."),
            NotificationKind.Cancelled => (
                $"Booking cancelled — {resourceName}",
                $"Your booking for {resourceName} on {when} was cancelled."),
            NotificationKind.Reminder => (
                $"Reminder — {resourceName}",
                $"This is a reminder that your booking for {resourceName} on {when} is coming up."),
            NotificationKind.ApprovalRequested => (
                $"Approval needed — {resourceName}",
                $"A booking for {resourceName} on {when} needs your approval."),
            NotificationKind.NoShowReleased => (
                $"Booking released — {resourceName}",
                $"Your booking for {resourceName} on {when} was released as a no-show; the slot is bookable again."),
            NotificationKind.ApprovalExpired => (
                $"Approval request expired — {resourceName}",
                $"Your request for {resourceName} on {when} expired before anyone decided on it."),
            _ => throw new InvalidOperationException(
                $"Notification kind '{kind}' is not anchored to a Booking."),
        };

    private static (string Subject, string Body) ComposeForRecurrenceRule(
        NotificationKind kind, string resourceName, DateOnly? occurrenceDate) =>
        kind switch
        {
            NotificationKind.RecurrenceOccurrenceSkipped => (
                $"A recurring booking was skipped — {resourceName}",
                $"The occurrence of your recurring booking for {resourceName} on {occurrenceDate:yyyy-MM-dd} "
                    + "falls in a daylight-saving change and was skipped. No booking was made for that date."),
            NotificationKind.SeriesCancelled => (
                $"Recurring series cancelled — {resourceName}",
                $"Your recurring booking series for {resourceName} was cancelled."),
            _ => throw new InvalidOperationException(
                $"Notification kind '{kind}' is not anchored to a RecurrenceRule."),
        };
}
