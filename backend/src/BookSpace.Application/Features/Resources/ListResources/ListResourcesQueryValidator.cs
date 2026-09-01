using BookSpace.Application.Common.Pagination;
using FluentValidation;

namespace BookSpace.Application.Features.Resources.ListResources;

// Paging and sorting only — there is nothing else on this query to judge.
// Typed against the concrete query type, per CLAUDE.md §12's discovery gotcha:
// IValidator<T> is invariant, so a validator over IPagedQuery would never be
// resolved for this one.
public sealed class ListResourcesQueryValidator : AbstractValidator<ListResourcesQuery>
{
    public ListResourcesQueryValidator()
    {
        this.AddPagingRules(ResourceSortFields.All);
    }
}
