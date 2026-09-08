using BookSpace.Application.Abstractions;
using BookSpace.Application.Common.Pagination;
using BookSpace.Domain.Enums;
using FluentValidation;

namespace BookSpace.Application.Features.Bookings.ListBookings;

// Paging, sorting, the range filter, and the two admin-only parameters.
//
// **This validator has a dependency**, which is a first in this codebase: it
// injects ICurrentUser to refuse a plain member sending `userId` or
// `scope=tenant`. That works because AddApplication registers each
// IValidator<T> by type through DI rather than as an instance, so constructor
// injection is activated normally.
//
// It is here rather than in the handler because the settled answer is
// **ValidationFailed 400 with a per-field error** (owner's call, 2026-09-08):
// ErrorKind has no Forbidden and inventing one for a query-string filter is
// heavier than the problem, and throwing a FluentValidation.ValidationException
// by hand from a handler to get the same body would be a worse way to say the
// same thing. Silently ignoring the parameter was rejected — a member would get
// a 200 answering a different question than they asked.
//
// The handler re-derives the privilege independently (BookingReadRules), so this
// is the client-facing message rather than the gate. See that file for why both.
//
// Typed against the concrete query type, per CLAUDE.md §12's discovery gotcha:
// IValidator<T> is invariant, so a validator over IPagedQuery would never be
// resolved for this one.
public sealed class ListBookingsQueryRequestValidator : AbstractValidator<ListBookingsQueryRequest>
{
    public ListBookingsQueryRequestValidator(ICurrentUser currentUser)
    {
        this.AddPagingRules(BookingSortFields.All);

        // An inverted range would silently return nothing, which reads as "you
        // have no bookings" — a wrong answer rather than an error. Equal is
        // refused too: a zero-width window can overlap nothing, so it is never
        // what the caller meant. Only checked when both are supplied; either one
        // alone is an open-ended range and cannot be inverted.
        RuleFor(q => q.To)
            .GreaterThan(q => q.From)
            .When(q => q.From.HasValue && q.To.HasValue)
            .WithMessage("To must be after From.");

        // Same reasoning as the blackout list filter: an instant with no zone can
        // only be interpreted by guessing one, and a silently shifted filter
        // window is a wrong answer with no error.
        RuleFor(q => q.From)
            .Must(CarryAZone)
            .When(q => q.From.HasValue)
            .WithMessage("From must carry a UTC designator or an explicit offset.");

        RuleFor(q => q.To)
            .Must(CarryAZone)
            .When(q => q.To.HasValue)
            .WithMessage("To must carry a UTC designator or an explicit offset.");

        // Bound by name from the query string, so an unparseable value never
        // reaches here — model binding fails first. This catches the numeric
        // form: Enum.TryParse accepts any integer, so `?status=99` would bind to
        // an undefined BookingStatus matching no row, and read as "you have no
        // bookings" — the same silent wrong answer this file keeps refusing.
        RuleFor(q => q.Status)
            .IsInEnum()
            .When(q => q.Status.HasValue)
            .WithMessage($"Status must be one of: {string.Join(", ", Enum.GetNames<BookingStatus>())}.");

        RuleFor(q => q.Scope)
            .IsInEnum()
            .WithMessage($"Scope must be one of: {string.Join(", ", Enum.GetNames<BookingScope>())}.");

        // Decision 0002: only a TenantAdmin may look past their own bookings.
        // Two separate rules rather than one combined message, so the error names
        // the field the client actually sent.
        RuleFor(q => q.UserId)
            .Must(_ => IsTenantAdmin(currentUser))
            .When(q => q.UserId.HasValue)
            .WithMessage("Only a TenantAdmin may filter bookings by userId.");

        RuleFor(q => q.Scope)
            .Must(_ => IsTenantAdmin(currentUser))
            .When(q => q.Scope != BookingScope.Own)
            .WithMessage("Only a TenantAdmin may request a scope other than Own.");

        // Refused rather than given a precedence rule. `scope=tenant` says "do
        // not restrict by owner" and `userId` says "restrict to this one", so
        // together one of them has to be ignored — and a parameter that is
        // accepted and then ignored is the shape this file rejects everywhere
        // else. `userId` alone already searches the whole tenant for that member,
        // so nothing is lost by making the client pick.
        RuleFor(q => q.Scope)
            .Must((query, _) => !query.UserId.HasValue)
            .When(q => q.Scope != BookingScope.Own)
            .WithMessage("Specify either userId or a scope, not both — "
                + "userId already searches the whole tenant for that member.");
    }

    private static bool IsTenantAdmin(ICurrentUser currentUser) =>
        currentUser.IsInRole(Role.TenantAdmin);

    private static bool CarryAZone(DateTime? value) =>
        value!.Value.Kind != DateTimeKind.Unspecified;
}
