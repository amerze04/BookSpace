using BookSpace.Application.Features.Resources.CreateResource;
using BookSpace.Application.Features.Resources.GetResource;

namespace BookSpace.IntegrationTests.Resources;

// Cross-endpoint agreement, asserted field by field.
//
// These comparisons used to be `Assert.Equal(created, fetched)` — record
// equality, which worked only because POST and GET returned the *same* DTO. Under
// the 2026-09-01 convention each endpoint owns its own response type, so the
// compiler now refuses that comparison, which is the point: "the create body
// equals the read body" was an assumption inherited from type sharing rather than
// a property anyone had stated.
//
// Stating it explicitly is strictly better. It says which fields the two
// endpoints are contracted to agree on, and it keeps working when one of them
// grows a field the other does not — Phase 3's availability windows on the read
// detail being the concrete case.
internal static class ResourceResponseAssertions
{
    public static void AssertSameResource(
        CreateResourceCommandResponse created,
        GetResourceQueryResponse fetched)
    {
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.Name, fetched.Name);
        Assert.Equal(created.Description, fetched.Description);
        Assert.Equal(created.ResourceType, fetched.ResourceType);
        Assert.Equal(created.Capacity, fetched.Capacity);
        Assert.Equal(created.TimeZoneId, fetched.TimeZoneId);
        Assert.Equal(created.RequiresApproval, fetched.RequiresApproval);
        Assert.Equal(created.MinDurationMinutes, fetched.MinDurationMinutes);
        Assert.Equal(created.MaxDurationMinutes, fetched.MaxDurationMinutes);
        Assert.Equal(created.IsArchived, fetched.IsArchived);
        Assert.Equal(created.CreatedAtUtc, fetched.CreatedAtUtc);
        Assert.Equal(created.UpdatedAtUtc, fetched.UpdatedAtUtc);
    }
}
