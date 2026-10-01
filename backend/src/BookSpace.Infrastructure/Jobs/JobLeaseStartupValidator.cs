using BookSpace.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BookSpace.Infrastructure.Jobs;

// Hardening pass, finding 9. A missing dbo.JobLeases row (a typo, or a
// migration that renamed one job's row but not every reference to its old
// name — RenameReminderDispatchJobLease is exactly that history) looks
// identical to ordinary lease contention from TryAcquireOrRenewAsync's own
// zero-rows-affected result: both mean "acquire failed," logged once at
// Debug and tried again next tick forever. Checked once, here, against
// every job name this build knows about (JobNames.All), so a drifted name
// fails the boot loudly instead of quietly running no job, ever.
//
// **An IHostedService, not inline code in Program.cs.** The obvious-looking
// alternative — a DB check placed directly between `builder.Build()` and
// `app.Run()` — was tried first and found to interact badly with
// `WebApplicationFactory`'s special interception of a minimal-API
// `Program.cs` (used by every integration test in this solution): an async
// operation there could leave the TestServer's request pipeline never
// configured, surfacing as "the server has not been started" on every HTTP
// call in the suite, sometimes fast, once as a long hang against a real
// connection. A hosted service's StartAsync is the host's own designated
// place for "run once before serving traffic," which the real host (and
// only the real host — AuthenticationTestHost strips every IHostedService,
// so this never runs there, and this check's own correctness is instead
// proven directly against IJobLeaseRepository in
// JobLeaseStartupValidationTests) handles correctly: a thrown exception here
// propagates out of the generic host's own StartAsync sequence and fails
// the boot exactly as intended, before any other hosted service (the three
// real jobs included) gets to start either.
internal sealed class JobLeaseStartupValidator : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public JobLeaseStartupValidator(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();

        var missing = await leases.FindMissingJobNamesAsync(JobNames.All, cancellationToken);

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"dbo.JobLeases is missing a row for: {string.Join(", ", missing)}. "
                + "Every name in BookSpace.Infrastructure.Jobs.JobNames must have a seeded row "
                + "(see the AddJobLeases migration) or that job can never acquire its lease.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
