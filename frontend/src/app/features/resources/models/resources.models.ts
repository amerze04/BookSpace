// Wire types mirroring the backend's resource DTOs exactly (field names,
// nullability, and enum spelling) so this file is the one place to check
// when the API shape changes, rather than re-deriving it at each call site.

// BookSpace.Domain.Enums.ResourceType. Serializes as its name, not an
// ordinal, because Program.cs registers JsonStringEnumConverter app-wide
// (WP-3 Phase 3). It carries no behaviour on the backend and none here
// either — see the enum's own comment in Resource.cs: Capacity, not
// ResourceType, is what determines exclusive vs. pooled.
export const RESOURCE_TYPES = ['Room', 'Equipment', 'Vehicle', 'LabSlot', 'Other'] as const;
export type ResourceType = (typeof RESOURCE_TYPES)[number];

// The `sort` whitelist ResourceSortFields declares server-side
// (docs/decisions/0015). Kept here so a sort control can only ever offer a
// value the API actually accepts.
export const RESOURCE_SORT_FIELDS = ['name', 'resourceType', 'capacity'] as const;
export type ResourceSortField = (typeof RESOURCE_SORT_FIELDS)[number];

// BookSpace.Domain enums serialize by name too — DayOfWeek is a BCL enum,
// not one of this codebase's own, but JsonStringEnumConverter is registered
// globally and doesn't distinguish.
export type DayOfWeekName =
  | 'Sunday'
  | 'Monday'
  | 'Tuesday'
  | 'Wednesday'
  | 'Thursday'
  | 'Friday'
  | 'Saturday';

// One row of GET /resources — ListResourcesQueryResponse. A summary, not the
// whole aggregate: no description or duration limits here, exactly as the
// backend's own comment on that record explains (a list of forty resources
// isn't forty copies of a long description).
export interface ResourceSummary {
  id: string;
  name: string;
  resourceType: ResourceType;
  capacity: number;
  timeZoneId: string;
  requiresApproval: boolean;
  isArchived: boolean;
}

// One weekly availability window on the detail read —
// AvailabilityWindowDetail. OpensAt/ClosesAt are resource-local wall-clock
// time ("HH:mm:ss", System.Text.Json's default TimeOnly format), read
// against the enclosing ResourceDetail.timeZoneId
// (docs/decisions/0003-availability-timezone.md) — never UTC.
export interface AvailabilityWindowDetail {
  id: string;
  weekday: DayOfWeekName;
  opensAt: string;
  closesAt: string;
}

// One assigned approver on the detail read — ApproverDetail. Name only, no
// email: a member deciding whether to book an approval-gated resource needs
// to know who decides, not how to contact them.
export interface ApproverDetail {
  userId: string;
  fullName: string;
}

// GET /resources/{id} — GetResourceQueryResponse. The full read detail,
// including the two collections a list row never carries.
export interface ResourceDetail {
  id: string;
  name: string;
  description: string | null;
  resourceType: ResourceType;
  capacity: number;
  timeZoneId: string;
  requiresApproval: boolean;
  minDurationMinutes: number | null;
  maxDurationMinutes: number | null;
  isArchived: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
  availabilityWindows: AvailabilityWindowDetail[];
  approvers: ApproverDetail[];
}

// GET /resources query params — ListResourcesQueryRequest. All optional:
// an omitted field is left off the request entirely so the backend's own
// documented default applies (PagingDefaults.Page/PageSize, "every type",
// includeArchived=false, "every approval state"), rather than this file
// inventing a second copy of those defaults that could drift from the
// server's.
//
// search and requiresApproval added 2026-09-15, once GET /resources actually
// supported them — see CLAUDE.md's "Resource list filters extended for WP-7"
// entry. Before that, the resource list screen filtered both client-side
// over one fetched page; that workaround is gone now that the server can
// answer directly.
export interface ListResourcesParams {
  page?: number;
  pageSize?: number;
  sort?: string;
  includeArchived?: boolean;
  type?: ResourceType;
  search?: string;
  requiresApproval?: boolean;
}

// ---- Write contracts (admin console phase 3) ----
//
// Reads had this file to themselves until now, because nothing in the app
// wrote a resource. These mirror ResourcesController's own request and response
// records, and the notes are about where the wire shape is surprising rather
// than about what each field means.

// POST /resources — CreateResourceRequest. No id and no orgId: the server
// assigns the first and takes the second from the token, so neither can be
// forged by editing a body.
//
// `requiresApproval` is on the wire and is always `false` from the create
// screen — FR-3.3 refuses a new resource that requires approval, because it
// cannot have approvers yet and they are assigned by a different endpoint. The
// field exists here because the API has it, not because the form can set it.
export interface CreateResourceRequest {
  name: string;
  description: string | null;
  resourceType: ResourceType;
  capacity: number;
  timeZoneId: string;
  requiresApproval: boolean;
  minDurationMinutes: number | null;
  maxDurationMinutes: number | null;
}

// PUT /resources/{id} — UpdateResourceRequest. **A full representation, not a
// patch** (docs/decisions/0015): every mutable field is supplied and an omitted
// nullable one means cleared. A form that sent only what changed would silently
// wipe the rest. The id travels in the route, not the body, so the two cannot
// disagree.
export type UpdateResourceRequest = CreateResourceRequest;

// What an edit can do that its own fields do not show — TimeZoneChangeNotice.
//
// Changing TimeZoneId **reinterprets** every existing availability window
// rather than shifting it, because a window is stored as resource-local
// wall-clock time (docs/decisions/0003): 09:00–17:00 stays 09:00–17:00 and
// starts meaning a different instant. Defensible but surprising, which is why
// the server reports it instead of leaving an admin to discover it from a
// booking that lands an hour off.
//
// Null whenever the timezone did not change, which is the normal case.
export interface TimeZoneChangeNotice {
  previousTimeZoneId: string;
  newTimeZoneId: string;
  reinterpretedAvailabilityWindowCount: number;
}

// The 201 body of POST /resources — CreateResourceCommandResponse. Carries no
// availability windows or approvers: a client has no use for a window list it
// has not created yet.
export interface CreateResourceResponse {
  id: string;
  name: string;
  description: string | null;
  resourceType: ResourceType;
  capacity: number;
  timeZoneId: string;
  requiresApproval: boolean;
  minDurationMinutes: number | null;
  maxDurationMinutes: number | null;
  isArchived: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

// The 200 body of PUT /resources/{id} — flat, with the notice alongside the
// fields rather than wrapping them.
export interface UpdateResourceResponse extends CreateResourceResponse {
  timeZoneChange: TimeZoneChangeNotice | null;
}

// The 200 body of POST /resources/{id}/archive. FR-3.5 preserves the resource,
// so the useful reply is the row with isArchived flipped rather than a 204.
export type ArchiveResourceResponse = CreateResourceResponse;
