namespace BookSpace.Application.Features.BlackoutPeriods;

// The `sort` whitelist for GET /resources/{id}/blackout-periods
// (docs/decisions/0015). Shared by the query's validator, which rejects anything
// not here, and the repository, which maps a canonical name onto a typed
// OrderBy — neither invents its own list.
//
// Spelled as the fields appear in the response JSON. Both are the interval's
// own ends, which is the only ordering a blackout list has a natural reading
// order by; Reason is free text and CreatedAtUtc says when the admin typed it,
// neither of which anyone browses a schedule by.
public static class BlackoutPeriodSortFields
{
    public const string StartsAtUtc = "startsAtUtc";
    public const string EndsAtUtc = "endsAtUtc";

    public static readonly IReadOnlyCollection<string> All = [StartsAtUtc, EndsAtUtc];
}
