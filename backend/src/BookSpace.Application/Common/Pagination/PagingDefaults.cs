namespace BookSpace.Application.Common.Pagination;

// WP-3 task 6 ("clean DTOs, error contracts, and pagination"). One place for
// the numbers so no endpoint invents its own — see
// docs/decisions/0015-api-contract-and-pagination.md.
public static class PagingDefaults
{
    public const int Page = 1;

    // Big enough that a tenant's resource list is usually one request, small
    // enough that a careless client can't ask for everything by accident.
    public const int PageSize = 20;

    // A ceiling, not a suggestion: PageSize above this is a validation failure,
    // not silently clamped. Clamping hides the bug from whoever wrote the client.
    public const int MaxPageSize = 100;
}
