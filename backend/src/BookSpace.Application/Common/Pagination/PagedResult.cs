namespace BookSpace.Application.Common.Pagination;

// The one response shape every list endpoint returns, so a client learns it
// once. Offset paging with a total count, decided by the repo owner on
// 2026-08-31: the datasets here are tens of rows, so neither the extra
// COUNT(*) nor deep-offset cost matters, and the client gets page numbers and
// a result total in exchange. See
// docs/decisions/0015-api-contract-and-pagination.md.
//
// TotalPages/HasPreviousPage/HasNextPage are computed rather than stored —
// they are derivable, and three stored copies of the same fact is three ways
// to disagree. They still serialize: System.Text.Json includes read-only
// properties.
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    // Zero rows is zero pages, not one empty page — so HasNextPage is false on
    // an empty result rather than pointing at a page that isn't there.
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> Empty(int page, int pageSize) => new([], page, pageSize, 0);
}
