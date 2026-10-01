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
//
// **The real delivery contract, stated plainly (hardening pass, finding 4):
// at-least-once, not exactly-once.** `UQ_Notifications_Once` makes the
// *row* idempotent — it cannot exist twice — but the row and the email it
// produces are two different things. If this process dies after
// `IEmailSender.SendAsync` returns delivered but before
// `RecordOutcomeAsync` commits `SentAtUtc`, the row is still unsent from the
// database's point of view, and a later run sends it again: a genuine
// duplicate email, not merely a duplicate database write. Nothing in this
// design closes that window — doing so would need either a
// two-phase-commit-style protocol the SMTP protocol has no primitive for, or
// a provider that accepts an idempotency key and guarantees dedup on it
// (neither of which this deployment has). `EmailMessage.IdempotencyKey`
// (a stable, deterministic Message-ID keyed on the notification row's own
// id) is a real mitigation, not a fix — it gives a mail admin something
// stable to search on, and it costs a retried send nothing, but nothing
// requires a receiving mail server to deduplicate on it. Accepted rather
// than solved: an occasional duplicate reminder or confirmation email is a
// tolerable cost next to the alternative (introducing a message broker or a
// transactional-outbox-with-provider-idempotency scheme) for what this
// application actually needs from email.
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
    // RenameReminderDispatchJobLease) exactly — via JobNames, the one place
    // that name is spelled (hardening pass, finding 9: this exact rename is
    // the drift risk that motivated it).
    protected override string JobName => JobNames.NotificationDispatch;

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
            JobName,
            OwnerId,
            cancellationToken);

        var succeeded = 0;
        var failed = 0;

        foreach (var notification in claimed)
        {
            try
            {
                var message = await notifications.BuildEmailAsync(notification, nowUtc, cancellationToken);

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
                //
                // Hardening pass, finding 7: logged here too, not only
                // written to the row's own LastError — an operator watching
                // application logs previously had no way to learn *which*
                // notification failed or why from the run summary alone.
                Logger.LogWarning(
                    ex, "{JobName} failed to dispatch notification {NotificationId} (kind {Kind})",
                    JobName, notification.Id, notification.Kind);
                await notifications.RecordOutcomeAsync(
                    notification.Id, nowUtc, succeeded: false, ex.Message, cancellationToken);
                failed++;
            }
        }

        return new JobRunSummary(claimed.Count, succeeded, failed);
    }
}
