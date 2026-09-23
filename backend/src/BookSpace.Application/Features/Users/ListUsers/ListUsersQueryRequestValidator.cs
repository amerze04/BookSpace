using BookSpace.Application.Common.Pagination;
using FluentValidation;

namespace BookSpace.Application.Features.Users.ListUsers;

// Typed against the concrete query type, per CLAUDE.md §12's discovery gotcha:
// IValidator<T> is invariant, so a validator over IPagedQuery would never be
// resolved for this one.
public sealed class ListUsersQueryRequestValidator : AbstractValidator<ListUsersQueryRequest>
{
    // Users.FullName is nvarchar(200) and Users.Email nvarchar(320); a search
    // term longer than the wider of the two cannot match anything, so the bound
    // is the email column's. MaximumLength is a no-op on null, same as
    // ListResourcesQueryRequestValidator's own search rule, so this needs no
    // .When guard for the "search omitted" case.
    private const int SearchMaxLength = 320;

    public ListUsersQueryRequestValidator()
    {
        this.AddPagingRules(UserSortFields.All);

        RuleFor(q => q.Search).MaximumLength(SearchMaxLength);
    }
}
