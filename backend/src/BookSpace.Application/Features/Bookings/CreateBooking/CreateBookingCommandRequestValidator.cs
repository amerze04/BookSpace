using FluentValidation;

namespace BookSpace.Application.Features.Bookings.CreateBooking;

// Shape only. Everything that needs the resource, the clock or the calendar is a
// rule check in the handler carrying a reason code — the duration limits, the
// elapsed interval, availability, blackouts and capacity all depend on state
// this validator cannot see.
//
// The instant rules are the same three BlackoutPeriodFieldRules applies, and for
// the same reasons; they are restated here rather than shared because that
// helper is typed against IBlackoutPeriodWriteCommand and generalising it for
// one more caller would be a shared abstraction with two users and no third in
// sight. Phase 2's cancel takes no instants at all.
public sealed class CreateBookingCommandRequestValidator : AbstractValidator<CreateBookingCommandRequest>
{
    // Matches Bookings.Title NVARCHAR(200), restated so an over-long title is a
    // 400 naming the field rather than a truncation or a SQL error.
    public const int MaxTitleLength = 200;

    public CreateBookingCommandRequestValidator()
    {
        RuleFor(c => c.ResourceId)
            .NotEmpty()
            .WithMessage("ResourceId is required.");

        // CK_Bookings_Interval, restated as a per-field message so it is a 400
        // instead of a 500 from the Booking constructor's ArgumentException.
        RuleFor(c => c.EndsAtUtc)
            .GreaterThan(c => c.StartsAtUtc)
            .WithMessage("EndsAtUtc must be after StartsAtUtc.");

        // Both columns are datetime2(0), so a sub-second value would be *rounded*
        // on write and the response would disagree with the row a client reads
        // back (CLAUDE.md §4.3). Rejected rather than truncated, matching how an
        // oversized pageSize is rejected rather than clamped (decision 0015).
        //
        // It matters more here than anywhere else so far: a booking's bounds are
        // compared against other bookings' bounds under a lock, so half a second
        // of drift is the difference between an overlap and an adjacency.
        RuleFor(c => c.StartsAtUtc)
            .Must(BeAWholeNumberOfSeconds)
            .WithMessage("StartsAtUtc must not carry fractional seconds.");

        RuleFor(c => c.EndsAtUtc)
            .Must(BeAWholeNumberOfSeconds)
            .WithMessage("EndsAtUtc must not carry fractional seconds.");

        // An instant with no zone means nothing and is refused. System.Text.Json
        // maps a trailing Z to Utc and an explicit offset to Local (converted
        // correctly), but a bare local-looking timestamp to Unspecified — which a
        // handler could only interpret by guessing a zone, most likely the
        // server's. The booking would silently cover the wrong hours.
        RuleFor(c => c.StartsAtUtc)
            .Must(CarryAZone)
            .WithMessage("StartsAtUtc must carry a UTC designator or an explicit offset.");

        RuleFor(c => c.EndsAtUtc)
            .Must(CarryAZone)
            .WithMessage("EndsAtUtc must carry a UTC designator or an explicit offset.");

        // CK_Bookings_Quantity. No upper bound here on purpose: what is too many
        // depends on the resource's Capacity, which this cannot see, and the
        // procedure refuses it with CapacityExceeded — a 409 that says how much
        // room there was, rather than a 400 that could only guess.
        RuleFor(c => c.Quantity)
            .GreaterThan(0)
            .WithMessage("Quantity must be greater than zero.");

        // Optional: a booking with no title is legal (the column is nullable),
        // since "Room 3, 14:00" is often all there is to say.
        RuleFor(c => c.Title)
            .MaximumLength(MaxTitleLength)
            .When(c => c.Title is not null);
    }

    private static bool BeAWholeNumberOfSeconds(DateTime value) =>
        value.Ticks % TimeSpan.TicksPerSecond == 0;

    private static bool CarryAZone(DateTime value) =>
        value.Kind != DateTimeKind.Unspecified;
}
