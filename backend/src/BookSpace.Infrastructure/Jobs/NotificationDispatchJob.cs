using BookSpace.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookSpace.Infrastructure.Jobs;

// WP-8 Phase 2 (docs/wp8-plan.md), FR-8.1/FR-9.2. Sends every kind of
// notification this application has ever queued — not only reminders, hence
// the class name rather than "ReminderDispatchJob" (see the
// RenameReminderDispatchJobLease migration for why the lease row's own name
// changed to match). One claim per tick, one email per claimed row, isolated
// from each other so a bad row never aborts the batch.
public sealed class NotificationDispatchJob : PeriodicJobRunner
{
    private readonly IOptionsMonitor<NotificationDispatchOptions> _options;

    public NotificationDispatchJob(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDispatchJob> logger,
        IOptionsMonitor<NotificationDispatchOptions> options)
        : base(scopeFactory, logger)
    {
        _options = options;
    }

    // Matches the seeded dbo.JobLeases row (AddJobLeases, renamed by
    // RenameReminderDispatchJobLease) exactly.
    protected override string JobName => "NotificationDispatch";

    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(_options.CurrentValue.PollSeconds);

    protected override TimeSpan LeaseDuration => TimeSpan.FromSeconds(_options.CurrentValue.LeaseSeconds);

    protected override async Task<JobRunSummary> RunAsync(
        IServiceProvider scopedServices, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var notifications = scopedServices.GetRequiredService<INotificationRepository>();
        var emailSender = scopedServices.GetRequiredService<IEmailSender>();

        var claimed = await notifications.ClaimDueAsync(
            options.BatchSize,
            options.MaxAttempts,
            TimeSpan.FromSeconds(options.BackoffBaseSeconds),
            nowUtc,
            cancellationToken);

        var succeeded = 0;
        var failed = 0;

        foreach (var notification in claimed)
        {
            try
            {
                var message = await notifications.BuildEmailAsync(notification, cancellationToken);

                if (message is null)
                {
                    // Decision D10 (docs/wp8-plan.md): moot, not a failure —
                    // e.g. a Reminder whose Booking is no longer Confirmed.
                    // Marked handled so it is never claimed again.
                    await notifications.RecordOutcomeAsync(
                        notification.Id, nowUtc, succeeded: true, error: null, cancellationToken);
                    succeeded++;
                    continue;
                }

                var result = await emailSender.SendAsync(message, cancellationToken);
                await notifications.RecordOutcomeAsync(
                    notification.Id, nowUtc, result.Delivered, result.FailureDetail, cancellationToken);

                if (result.Delivered)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // "Isolate failure per item" (WP-8's own wording): one bad
                // notification — a broken join, an unexpected null — is
                // caught here rather than aborting every row still queued
                // behind it in this batch. Recorded as a failed attempt like
                // any other, so it is retried with the same backoff/cap as a
                // genuine transient send failure.
                await notifications.RecordOutcomeAsync(
                    notification.Id, nowUtc, succeeded: false, ex.Message, cancellationToken);
                failed++;
            }
        }

        return new JobRunSummary(claimed.Count, succeeded, failed);
    }
}
