namespace BookSpace.Application.Features.Users;

// The `sort` whitelist for GET /users (docs/decisions/0015-api-contract-and-
// pagination.md). Shared by ListUsersQueryRequestValidator, which rejects
// anything not here, and the repository, which maps a canonical name onto a
// typed OrderBy — neither invents its own list.
//
// Spelled the way the field appears in the response JSON, so `sort=fullName`
// names something the client can actually see.
//
// Deliberately two entries, and deliberately not `roles`: a user's roles are an
// owned collection, so ordering by them would mean ordering by "the first row of
// a child table in whatever order SQL Server returned it" — a sort nobody could
// describe, over a set the picker shows as an unordered list of badges anyway.
public static class UserSortFields
{
    public const string FullName = "fullName";
    public const string Email = "email";

    public static readonly IReadOnlyCollection<string> All = [FullName, Email];
}
