using FluentValidation;

namespace BookSpace.Application.Features.Resources;

// Shape validation shared by CreateResourceCommandRequest and UpdateResourceCommandRequest,
// added by each concrete validator — same pattern as
// PagedQueryRules.AddPagingRules, and for the same reason (a base validator
// class would spend C#'s single inheritance slot on this).
//
// Lengths mirror the column widths in docs/bookspace-schema-v2.sql, so an
// oversized value comes back as a 400 naming the field rather than a 500 from
// SQL Server truncating. The tier-1 constraints are still the floor
// (CLAUDE.md §6): these are the message, not the guarantee.
//
// What is deliberately NOT here: whether TimeZoneId is a real IANA id. The
// shape validator cannot know the host's timezone list, so that is a rule check
// in the handler carrying ReasonCodes.InvalidTimeZone — see
// ResourceWriteRules.
public static class ResourceFieldRules
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;
    public const int ResourceTypeMaxLength = 50;
    public const int TimeZoneIdMaxLength = 60;

    public static void AddResourceFieldRules<TCommand>(this AbstractValidator<TCommand> validator)
        where TCommand : IResourceWriteCommand
    {
        validator.RuleFor(c => c.Name)
            .NotEmpty()
            .MaximumLength(NameMaxLength);

        validator.RuleFor(c => c.Description)
            .MaximumLength(DescriptionMaxLength);

        validator.RuleFor(c => c.ResourceType)
            .NotEmpty()
            .MaximumLength(ResourceTypeMaxLength);

        // CK_Resources_Capacity. Decision 0005: this is a count of concurrent
        // units, so zero would mean a resource nothing can ever be booked on.
        validator.RuleFor(c => c.Capacity)
            .GreaterThan(0);

        validator.RuleFor(c => c.TimeZoneId)
            .NotEmpty()
            .MaximumLength(TimeZoneIdMaxLength);

        // CK_Resources_DurationLimits, restated as per-field messages. Null on
        // either bound means "no limit", so the rules only fire when a value
        // was supplied.
        validator.RuleFor(c => c.MinDurationMinutes)
            .GreaterThan(0)
            .When(c => c.MinDurationMinutes is not null);

        validator.RuleFor(c => c.MaxDurationMinutes)
            .GreaterThan(0)
            .When(c => c.MaxDurationMinutes is not null);

        validator.RuleFor(c => c.MaxDurationMinutes)
            .GreaterThanOrEqualTo(c => c.MinDurationMinutes)
            .When(c => c.MinDurationMinutes is not null && c.MaxDurationMinutes is not null)
            .WithMessage("MaxDurationMinutes must be greater than or equal to MinDurationMinutes.");
    }
}
