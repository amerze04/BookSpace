using System.Linq.Expressions;
using BookSpace.Application.Common.Pagination;
using Microsoft.EntityFrameworkCore;

namespace BookSpace.Infrastructure.Persistence;

// Lives in Infrastructure, not next to PagedResult in Application: CountAsync
// and ToListAsync are EF Core, and BookSpace.Application deliberately has no EF
// dependency (CLAUDE.md §3).
public static class PagedQueryableExtensions
{
    // Runs two queries against the same IQueryable: COUNT(*) for the total, then
    // OFFSET/FETCH for the page. Both go through the caller's DbContext, so the
    // global query filters and RLS apply to both — a total count can never
    // include rows the page itself would hide.
    public static async Task<PagedResult<TResult>> ToPagedResultAsync<TResult>(
        this IQueryable<TResult> source,
        IPagedQuery query,
        CancellationToken cancellationToken = default)
    {
        RequireDeterministicOrder(source);

        var totalCount = await source.CountAsync(cancellationToken);

        var items = await source
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<TResult>(items, query.Page, query.PageSize, totalCount);
    }

    // Offset paging is only correct over a total order. Without ORDER BY, SQL
    // Server may return rows in any order it likes, so "rows 21-40" is
    // undefined and pages can silently overlap or skip; with an ORDER BY that
    // has ties, the same happens among the tied rows. EF issues no warning for
    // either, and the symptom is a wrong row on page 2 rather than an error,
    // so this turns the convention into something that fails loudly the first
    // time a query forgets it.
    //
    // It can only check that *an* ordering exists — that it ends in a unique
    // column (Id) is still the query author's job, and is documented in
    // docs/decisions/0015-api-contract-and-pagination.md.
    private static void RequireDeterministicOrder(IQueryable source)
    {
        if (!OrderingDetector.HasOrdering(source.Expression))
        {
            throw new InvalidOperationException(
                "ToPagedResultAsync requires an ordered query: apply OrderBy/OrderByDescending "
                + "(ending in a unique column, normally Id) before paging, or page boundaries "
                + "are undefined.");
        }
    }

    private sealed class OrderingDetector : ExpressionVisitor
    {
        private bool _found;

        public static bool HasOrdering(Expression expression)
        {
            var detector = new OrderingDetector();
            detector.Visit(expression);
            return detector._found;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable)
                && (node.Method.Name.StartsWith("OrderBy", StringComparison.Ordinal)
                    || node.Method.Name.StartsWith("ThenBy", StringComparison.Ordinal)))
            {
                _found = true;
            }

            return base.VisitMethodCall(node);
        }
    }
}
