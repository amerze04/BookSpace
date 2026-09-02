namespace BookSpace.Domain.Entities;

// One availability window as a caller asks for it, before it is an
// AvailabilityWindow. FR-3.2.
//
// It exists because windows are managed as a *set*: a resource's whole weekly
// schedule is replaced in one call (docs/wp3-plan.md), so
// Resource.ReplaceAvailabilityWindows takes a collection — and a collection of
// four loose parameters is not a thing C# can express.
//
// Id is supplied by the caller rather than minted here, matching
// AddAvailabilityWindow and the Resource constructor: nothing in the Domain
// project generates identifiers, so a test can pin them.
public readonly record struct AvailabilityWindowDefinition(
    Guid Id,
    DayOfWeek Weekday,
    TimeOnly OpensAt,
    TimeOnly ClosesAt);
