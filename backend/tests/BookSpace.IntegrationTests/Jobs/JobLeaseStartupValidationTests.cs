using BookSpace.Application.Abstractions;
using BookSpace.Infrastructure.Jobs;
using BookSpace.IntegrationTests.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace BookSpace.IntegrationTests.Jobs;

// Hardening pass, finding 9. Proves the repository method Program.cs's own
// startup check calls — not a full application-boot test: WebApplicationFactory
// wraps Program.cs's top-level statements in a way that swallows a thrown
// InvalidOperationException before it ever reaches a test (the same
// catch-all that already logs-and-swallows every other ValidateOnStart()
// failure in this file, a pre-existing pattern this hardening pass did not
// introduce and is not the thing under test here), which would make a
// boot-failure assertion unreliable at best. What is reliably testable, and
// is the actual new logic, is FindMissingJobNamesAsync itself.
[Collection(nameof(AuthenticationTestCollection))]
public sealed class JobLeaseStartupValidationTests
{
    private readonly AuthenticationTestHost _host;

    public JobLeaseStartupValidationTests(AuthenticationTestHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task EveryRealJobNameHasASeededRow()
    {
        await using var scope = _host.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();

        var missing = await leases.FindMissingJobNamesAsync(JobNames.All, CancellationToken.None);

        Assert.Empty(missing);
    }

    // The exact failure mode finding 9 is about: a name that exists in code
    // but has no corresponding seeded row must be reported, not silently
    // treated as "held by another instance".
    [Fact]
    public async Task AFictitiousJobNameIsReportedMissing()
    {
        await using var scope = _host.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<IJobLeaseRepository>();

        var fictitious = $"does-not-exist-{Guid.NewGuid():n}";
        var missing = await leases.FindMissingJobNamesAsync(
            [.. JobNames.All, fictitious], CancellationToken.None);

        Assert.Equal([fictitious], missing);
    }
}
