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
    // UPDATE TOP (n) has no defined row order (SQL Server picks arbitrarily
    // among matches), so this batch is not guaranteed oldest-due-first.
    // Accepted: nothing here promises an order, only that every due row is
    // eventually claimed across however many ticks it takes.
    public async Task<IReadOnlyList<ClaimedNotification>> ClaimDueAsync(
        int batchSize, int maxAttempts, TimeSpan backoffBase, DateTime nowUtc, CancellationToken cancellationToken)
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
                UPDATE TOP (@BatchSize) dbo.Notifications WITH (UPDLOCK, READPAST)
                SET Attempts = Attempts + 1,
                    UpdatedAtUtc = @NowUtc
                OUTPUT inserted.Id, inserted.BookingId, inserted.RecurrenceRuleId,
                       inserted.OccurrenceDate, inserted.RecipientUserId, inserted.Kind,
                       inserted.Attempts
                WHERE SentAtUtc IS NULL
                  AND SendAtUtc <= @NowUtc
                  AND Attempts < @MaxAttempts
                  AND (Attempts = 0
                       OR DATEADD(SECOND, CAST(POWER(2, Attempts) * @BackoffBaseSeconds AS BIGINT), UpdatedAtUtc) <= @NowUtc)
                """;
            command.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction();

            command.Parameters.Add(new SqlParameter("@BatchSize", SqlDbType.Int) { Value = batchSize });
            command.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
            command.Parameters.Add(new SqlParameter("@MaxAttempts", SqlDbType.Int) { Value = maxAttempts });
            command.Parameters.Add(new SqlParameter("@BackoffBaseSeconds", SqlDbType.Int)
            {
                Value = (int)backoffBase.TotalSeconds,
            });

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
    public async Task<EmailMessage?> BuildEmailAsync(ClaimedNotification notification, CancellationToken cancellationToken)
    {
        using var _ = TenantBypassScope.Enter();

        var recipient = await _context.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Id == notification.RecipientUserId)
            .Select(u => new { u.Email, u.FullName })
            .FirstAsync(cancellationToken);

        var to = new EmailAddress(recipient.Email, recipient.FullName);

        if (notification.BookingId is { } bookingId)
        {
            var booking = await _context.Bookings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(b => b.Id == bookingId)
                .Select(b => new { b.ResourceId, b.StartsAtUtc, b.EndsAtUtc, b.Status })
                .FirstAsync(cancellationToken);

            // Decision D10 (docs/wp8-plan.md): a Reminder whose Booking is no
            // longer Confirmed — cancelled or rejected before it fired — has
            // nothing left to remind about. The caller marks this handled,
            // not sent, and it is never retried.
            if (notification.Kind == NotificationKind.Reminder && booking.Status != BookingStatus.Confirmed)
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

            return new EmailMessage(to, subject, body);
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

        return new EmailMessage(to, ruleSubject, ruleBody);
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
    // guessing: a new Booking-anchored kind added later (ApprovalExpired,
    // WP-8 Phase 4) needs a line here before it can ever be emailed, the same
    // "one arm per reason, default throws" shape ReasonCodes' own mapping
    // switches use.
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
