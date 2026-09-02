using BookSpace.Application.Common.Pagination;
using FluentValidation;

namespace BookSpace.Application.Features.BlackoutPeriods.ListBlackoutPeriods;

// Paging, sorting, and the one thing the range filter can get wrong. Typed
// against the concrete query type, per CLAUDE.md §12's discovery gotcha:
// IValidator<T> is invariant, so a validator over IPagedQuery would never be
// resolved for this one.
public sealed class ListBlackoutPeriodsQueryRequestValidator
    : AbstractValidator<ListBlackoutPeriodsQueryRequest>
{
    public ListBlackoutPeriodsQueryRequestValidator()
    {
        this.AddPagingRules(BlackoutPeriodSortFields.All);

        RuleFor(q => q.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // An inverted range would silently return nothing, which reads as "this
        // resource has no blackouts" — a wrong answer rather than an error.
        // Equal is refused too: a zero-width window can overlap nothing, so it is
        // never what the caller meant.
        //
        // Only checked when both are supplied; either one alone is an open-ended
        // range and cannot be inverted.
        RuleFor(q => q.To)
            .GreaterThan(q => q.From)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("To must be after From.");

        // Same reasoning as the create command: an instant with no zone can only
        // be interpreted by guessing one. A filter is lower-stakes than a stored
        // interval — the worst case is the wrong rows rather than the wrong
        // stored hours — but a silently shifted filter window is still a wrong
        // answer with no error, so it is refused here too.
        RuleFor(q => q.From)
            .Must(CarryAZone)
            .When(q => q.From.HasValue)
            .WithMessage("From must carry a UTC designator or an explicit offset.");

        RuleFor(q => q.To)
            .Must(CarryAZone)
            .When(q => q.To.HasValue)
            .WithMessage("To must carry a UTC designator or an explicit offset.");
    }

    private static bool CarryAZone(DateTime? value) =>
        value!.Value.Kind != DateTimeKind.Unspecified;
}
