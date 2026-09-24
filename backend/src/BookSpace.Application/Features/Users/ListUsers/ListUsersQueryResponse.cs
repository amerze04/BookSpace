using BookSpace.Domain.Enums;

namespace BookSpace.Application.Features.Users.ListUsers;

// One row of GET /users, serving both of its callers — the approvers picker
// (`scope` omitted) and the user directory (`scope=All`).
//
// **Email is on the wire here and deliberately is not on ApproverSummary.** The
// difference is who is reading. ApproverSummary answers "who approves this
// room" for every member of the tenant, where an address is contact information
// nobody asked for; this endpoint is TenantAdmin-only and its job is picking one
// person out of a list, where two people called "A. Novak" are otherwise
// indistinguishable. The admin provisioned these accounts in the first place.
//
// Roles are included because decision `0018` gives the picker no other way to
// say anything true about eligibility — it cannot explain a refusal, since every
// reason collapses to ApproverNotEligible, so showing what makes each person
// eligible is the compensating information. The directory needs them for a
// different reason: roles are what it exists to manage.
//
// **IsActive, as of user management phase 4.** This comment used to end by
// listing IsActive among the things deliberately absent, on the grounds that
// "every row is active by construction — an inactive user is not eligible and
// never comes back". That stopped being true the moment `scope=All` existed: a
// directory has to show a deactivated account, or an admin cannot tell somebody
// who left from somebody who was never added. Left as it was, the comment would
// have been the most misleading kind — an accurate-sounding claim about a field
// that had quietly started varying.
//
// It is still true of the *default* scope, and that is worth knowing rather than
// asserting: every row of the picker's answer has IsActive true, because an
// inactive user is not an eligible approver. The picker can ignore the field.
//
// Serialized by name ("Approver", not 2) — Program.cs registers
// JsonStringEnumConverter app-wide.
//
// What is still not here: OrgId (the token's, or the row would not be visible),
// and anything from the audit columns.
public sealed record ListUsersQueryResponse(
    Guid Id,
    string FullName,
    string Email,
    bool IsActive,
    IReadOnlyList<Role> Roles);
