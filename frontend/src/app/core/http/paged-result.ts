// The one response shape every list endpoint returns
// (BookSpace.Application.Common.Pagination.PagedResult<T>, decisions/0015).
// TotalPages/HasPreviousPage/HasNextPage are computed properties on the
// backend record, but System.Text.Json serializes read-only properties too,
// so they really do arrive on the wire — mirrored here rather than
// recomputed client-side, so there is one source for them, not two that
// could disagree.
export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPreviousPage: boolean;
  hasNextPage: boolean;
}
