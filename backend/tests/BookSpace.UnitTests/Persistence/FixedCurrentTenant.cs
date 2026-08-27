using BookSpace.Application.Abstractions;

namespace BookSpace.UnitTests.Persistence;

internal sealed class FixedCurrentTenant : ICurrentTenant
{
    public FixedCurrentTenant(Guid? orgId)
    {
        OrgId = orgId;
    }

    public Guid? OrgId { get; }
}
