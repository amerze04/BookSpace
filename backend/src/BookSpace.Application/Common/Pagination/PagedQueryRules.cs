using FluentValidation;

namespace BookSpace.Application.Common.Pagination;

// Shared paging validation, added by each list query's own validator:
//
//     public sealed class ListResourcesQueryRequestValidator : AbstractValidator<ListResourcesQueryRequest>
//     {
//         public ListResourcesQueryRequestValidator() => this.AddPagingRules(SortableFields);
//     }
//
// An extension method rather than a base validator class, for two reasons.
// C# has single inheritance, so a base class would spend it on paging; and the
// discovery gotcha recorded in CLAUDE.md §12 means the rules have to end up on
// a validator typed against the *concrete* query type either way —
// IValidator<TQuery> is invariant, so nothing generic over IPagedQuery is ever
// resolved for a concrete query.
public static class PagedQueryRules
{
    public static void AddPagingRules<TQuery>(
        this AbstractValidator<TQuery> validator,
        IReadOnlyCollection<string> sortableFields)
        where TQuery : IPagedQuery
    {
        validator.RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Page must be 1 or greater.");

        // Rejected, not clamped: see PagingDefaults.MaxPageSize.
        validator.RuleFor(q => q.PageSize)
            .InclusiveBetween(1, PagingDefaults.MaxPageSize)
            .WithMessage($"PageSize must be between 1 and {PagingDefaults.MaxPageSize}.");

        validator.RuleFor(q => q.Sort)
            .Must(sort => SortOption.TryParse(sort, sortableFields, out _))
            .WithMessage($"Sort must be one of: {string.Join(", ", sortableFields)}, "
                + "optionally prefixed with '-' for descending.");
    }
}
