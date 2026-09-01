namespace BookSpace.Application.Common.Pagination;

// Implemented by every list query. Deliberately an interface over three plain
// properties rather than a base record: queries are records with their own
// filter parameters, and inheriting from a base would fix the order of their
// constructor parameters for no benefit.
//
// Defaults live on the implementing record's constructor parameters
// (`int Page = PagingDefaults.Page`), so a client that omits the query string
// gets page 1 of 20 without any handler-side normalization.
public interface IPagedQuery
{
    int Page { get; }

    int PageSize { get; }

    // Null means "the endpoint's own default order". Syntax is `field` for
    // ascending and `-field` for descending — see SortOption.
    string? Sort { get; }
}
