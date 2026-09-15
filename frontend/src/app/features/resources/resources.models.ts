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
// includeArchived=false), rather than this file inventing a second copy of
// those defaults that could drift from the server's.
export interface ListResourcesParams {
  page?: number;
  pageSize?: number;
  sort?: string;
  includeArchived?: boolean;
  type?: ResourceType;
}
