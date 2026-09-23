// Wire types for `GET /users` — the endpoint admin console phase 1 added, and
// the only one this feature owns outright. It lives here rather than beside the
// resource models because nothing outside the admin console can call it: it is
// TenantAdmin-only, and its whole reason for existing is the approvers picker.

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
// **The route is broader than the answer.** This is the decision `0018`
// eligible-approver set — own-tenant, active, holding `Approver` or
// `TenantAdmin` — not every user in the tenant, and there is no parameter that
// widens it. A Member is absent by design.
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

// `GET /users` query params. All optional; an omitted field lets the backend's
// own documented default apply rather than this file keeping a second copy of
// it (the convention `ListResourcesParams` established).
export interface ListUsersParams {
  page?: number;
  pageSize?: number;
  sort?: string;
  search?: string;
}
