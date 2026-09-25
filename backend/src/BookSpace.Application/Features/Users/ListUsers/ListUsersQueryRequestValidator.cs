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

        // Bound by name from the query string, so an unparseable value never
        // reaches here — model binding fails first. This catches the numeric
        // form, exactly as ListBookingsQueryRequestValidator does for its own
        // Scope: Enum.TryParse accepts any integer, so `?scope=99` would bind to
        // an undefined UserScope, fall through the repository's switch to the
        // eligible-approver branch, and read as "your tenant has two people" to
        // an admin who asked for the directory. A 400 naming the field beats a
        // silently narrowed answer.
        //
        // No role rule alongside it, unlike bookings: the whole controller is
        // TenantAdmin, so there is no weaker caller for a scope to be widened
        // past.
        RuleFor(q => q.Scope)
            .IsInEnum()
            .WithMessage($"Scope must be one of: {string.Join(", ", Enum.GetNames<UserScope>())}.");
    }
}
