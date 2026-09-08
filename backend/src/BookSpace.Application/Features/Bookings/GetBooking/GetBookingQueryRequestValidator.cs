using FluentValidation;

namespace BookSpace.Application.Features.Bookings.GetBooking;

// One field, one rule. Present so the query is not the only IRequest in the
// feature without a validator — CLAUDE.md §12 records that nothing checks at
// startup whether a request has one, so an absent validator and a deliberately
// empty one look identical from outside.
//
// Guid.Empty specifically: the route constraint is `{id:guid}`, so a malformed
// id is a 404 from routing before it reaches here, but all-zeros parses fine and
// would otherwise reach the database as a lookup that can only miss.
public sealed class GetBookingQueryRequestValidator : AbstractValidator<GetBookingQueryRequest>
{
    public GetBookingQueryRequestValidator()
    {
        RuleFor(q => q.BookingId)
            .NotEmpty()
            .WithMessage("BookingId is required.");
    }
}
