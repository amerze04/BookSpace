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

    // Hardening pass, P3: (Page - 1) * PageSize is plain int arithmetic in
    // ToPagedResultAsync, and with PageSize at its own maximum, a Page past
    // roughly 21.4 million overflows int and wraps to a negative offset —
    // which SQL Server's OFFSET clause rejects outright, an unhandled
    // SqlException reaching the client as a 500 rather than a 400 naming the
    // field. No realistic result set gets anywhere near this many pages;
    // rejecting is the same "ceiling, not a suggestion" philosophy
    // MaxPageSize already applies, not a new one.
    public const int MaxPage = 100_000;
}
