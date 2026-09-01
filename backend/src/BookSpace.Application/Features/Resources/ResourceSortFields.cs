namespace BookSpace.Application.Features.Resources;

// The `sort` whitelist for GET /resources (docs/decisions/0015-api-contract-
// and-pagination.md). Shared by ListResourcesQueryValidator, which rejects
// anything not here, and the repository, which maps a canonical name onto a
// typed OrderBy — neither invents its own list.
//
// Spelled the way the field appears in the response JSON, so `sort=capacity`
// names something the client can actually see. Deliberately short: every
// entry is API surface that has to keep working, and "sortable by whatever
// happens to be a column" is not a contract worth promising.
public static class ResourceSortFields
{
    public const string Name = "name";
    public const string ResourceType = "resourceType";
    public const string Capacity = "capacity";

    public static readonly IReadOnlyCollection<string> All = [Name, ResourceType, Capacity];
}
