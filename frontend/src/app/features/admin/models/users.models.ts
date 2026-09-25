// Wire types for `GET /users` — the endpoint admin console phase 1 added, and
// the only one this feature owns outright. It lives here rather than beside the
// resource models because nothing outside the admin console can call it: it is
// TenantAdmin-only, and its whole reason for existing is the approvers picker.
//
// User management phase 4 gave that endpoint a second job — the user directory,
// behind a `scope` parameter. These types describe the default scope only, and
// deliberately have not changed; see `EligibleUser` below.

// BookSpace.Domain.Enums.Role, serialized by name — Program.cs registers
// JsonStringEnumConverter app-wide.
//
// `SysAdmin` is in the union because the enum has it, not because this endpoint
// can return one: a SysAdmin has no `orgId` claim, so the tenant query filter
// excludes them from every tenant-scoped read. Leaving it out of the type would
// be a client-side claim about server behaviour that the type system would then
// enforce on nothing.
export type UserRole = 'SysAdmin' | 'TenantAdmin' | 'Approver' | 'Member';

// One row of `GET /users` — ListUsersQueryResponse.
//
// **This is the decision `0018` eligible-approver set** — own-tenant, active,
// holding `Approver` or `TenantAdmin` — not every user in the tenant. A Member
// is absent by design.
//
// It used to say "and there is no parameter that widens it", which stopped
// being true in user management phase 4: `GET /users?scope=All` returns every
// user in the tenant, deactivated accounts included, for the directory screen
// phase 6 will build. **The narrow set is still the default**, deliberately, so
// this type and its caller are unaffected — see `UserScope` on the backend for
// why the wider set is opt-in. When the directory lands it will need its own
// row type: the wire row now also carries `isActive`, which is omitted here
// because in this scope it is true of every row and the picker has no use for
// it.
//
// `email` is here and deliberately is *not* on `ApproverDetail`: that one
// answers "who approves this room" for every member of the tenant, where an
// address is contact information nobody asked for. This one is admin-only and
// its job is telling two people with the same name apart.
//
// `roles` is included because `0018` leaves the picker nothing else true to say
// about eligibility — every ineligibility reason collapses into one code, so
// showing what makes each person eligible is the compensating information.
export interface EligibleUser {
  id: string;
  fullName: string;
  email: string;
  roles: UserRole[];
}

// One row of `GET /users?scope=All` — the directory, user management phase 6.
//
// The same row the picker gets plus `isActive`, and it extends `EligibleUser`
// rather than repeating its fields so the relationship is in the type: the
// directory is a superset of the picker's answer, not a different shape.
//
// `isActive` is the field the directory exists for. Without it an administrator
// cannot tell somebody who left from somebody who was never added — and the
// picker has no use for it, because an inactive user is not an eligible
// approver and never appears there.
export interface DirectoryUser extends EligibleUser {
  isActive: boolean;
}

// Which set `GET /users` answers with — `UserScope` on the backend. Omitted
// means the eligible-approver set, and **that default is deliberate**: a
// forgotten parameter narrows rather than widens, so the approvers picker
// cannot start offering people `ReplaceApprovers` would then refuse.
export type UserScope = 'EligibleApprovers' | 'All';

// `GET /users` query params. All optional; an omitted field lets the backend's
// own documented default apply rather than this file keeping a second copy of
// it (the convention `ListResourcesParams` established) — which for `scope` is
// the point rather than a convenience.
export interface ListUsersParams {
  page?: number;
  pageSize?: number;
  sort?: string;
  search?: string;
  scope?: UserScope;
}

// The body of `POST /users`. No password and no roles: the recipient chooses
// the first through the activation link, and the server assigns `Member` as the
// second (user management phase 3).
export interface CreateUserRequest {
  email: string;
  fullName: string;
}

// The 201 body of `POST /users`.
//
// **`activationLink` is a live credential**, carried whether or not the
// invitation email went out. That is the server's deliberate choice — creating
// a colleague must not fail because an email provider is down, and nothing
// re-issues an invitation — and it makes this response something to show once
// and never store. The screen that renders it says so.
//
// `invitationEmailSent` is a field rather than something inferred from the
// status code, because a 201 arrives either way and the outcome reads
// differently: one is "we have told them", the other is "you will have to".
export interface CreatedUser {
  id: string;
  email: string;
  fullName: string;
  isActive: boolean;
  roles: UserRole[];
  createdAtUtc: string;
  activationLink: string;
  activationLinkExpiresAtUtc: string;
  invitationEmailSent: boolean;
}
