using BookSpace.Application.Messaging;

namespace BookSpace.Application.Features.Users.CreateUser;

// POST /users. TenantAdmin only (see UsersController).
//
// The authority is the PRD §2 persona line — a Tenant Administrator "Manage[s]
// resources, rules, **members, roles**" — plus FR-2.4. There is no functional
// requirement for user management and no FR number to cite here; see
// docs/user-management-plan.md §1, which says so rather than inventing one.
//
// No OrgId and no CreatedByUserId: both come from the token, via ICurrentTenant
// and ICurrentUser, exactly as CreateResourceCommandRequest does. Accepting
// either on the wire would make forging one a matter of editing a request body.
//
// **No password, and no roles either.**
//
// No password because nobody — least of all the administrator doing this —
// should choose a credential on somebody else's behalf. The account is created
// with an unguessable placeholder and the recipient sets their own through
// POST /auth/activate (phase 2).
//
// No roles because the plan left "should creation take an initial role set" as
// a phase-3 call, and the answer is that it should not *take* one but should
// *assign* one — see the handler. Editing roles is phase 5, with the
// last-TenantAdmin guard that belongs to it; a second write path for roles here
// would be a second place that guard has to hold.
public sealed record CreateUserCommandRequest(string Email, string FullName)
    : IRequest<CreateUserCommandResponse>;
