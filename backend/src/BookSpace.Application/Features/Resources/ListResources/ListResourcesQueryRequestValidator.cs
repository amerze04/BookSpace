using BookSpace.Application.Common.Pagination;
using BookSpace.Application.Features.Resources;
using FluentValidation;

namespace BookSpace.Application.Features.Resources.ListResources;

// Typed against the concrete query type, per CLAUDE.md §12's discovery gotcha:
// IValidator<T> is invariant, so a validator over IPagedQuery would never be
// resolved for this one.
public sealed class ListResourcesQueryRequestValidator : AbstractValidator<ListResourcesQueryRequest>
{
    public ListResourcesQueryRequestValidator()
    {
        this.AddPagingRules(ResourceSortFields.All);

        // Bounded to the field it's most likely matching against (Name), not
        // an arbitrary round number — MaximumLength is a no-op on null, same
        // as ResourceFieldRules.Description, so this needs no .When guard for
        // the "search omitted" case.
        RuleFor(q => q.Search).MaximumLength(ResourceFieldRules.NameMaxLength);
    }
}
