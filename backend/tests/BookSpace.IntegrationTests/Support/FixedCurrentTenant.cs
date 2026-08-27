using BookSpace.Application.Abstractions;

namespace BookSpace.IntegrationTests.Support;

// Test double for ICurrentTenant where a fixed value is enough — building a
// BookSpaceDbContext outside the DI container (no real HTTP request to read
// claims from).
public sealed class FixedCurrentTenant : ICurrentTenant
{
    public FixedCurrentTenant(Guid? orgId)
    {
        OrgId = orgId;
    }

    public Guid? OrgId { get; }
}
