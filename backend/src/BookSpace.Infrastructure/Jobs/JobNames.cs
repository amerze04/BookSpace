namespace BookSpace.Infrastructure.Jobs;

// Hardening pass, finding 9. This branch's own history is the evidence for
// why this exists: the seeded dbo.JobLeases row for the reminder/dispatch
// job was renamed from 'ReminderDispatch' to 'NotificationDispatch'
// (RenameReminderDispatchJobLease migration) partway through building it,
// and until that rename landed everywhere, the job name a class used and the
// row a migration seeded were two independently-edited strings that merely
// happened to agree. A single source for "every job name this application
// knows about" is what makes that agreement checkable — see
// `JobLeaseStartupValidation`, which walks this list at boot and fails loudly
// if any seeded row is missing, rather than letting a drifted name look
// exactly like ordinary lease contention forever.
public static class JobNames
{
    public const string NotificationDispatch = "NotificationDispatch";
    public const string NoShowRelease = "NoShowRelease";
    public const string StaleApprovalExpiry = "StaleApprovalExpiry";

    public static readonly IReadOnlyCollection<string> All =
    [
        NotificationDispatch,
        NoShowRelease,
        StaleApprovalExpiry,
    ];
}
