import { ResourceDetail, ResourceSummary, ResourceType } from '../../features/resources/resources.models';

// Extracted 2026-09-16 (WP-7 Phase 2, availability screen) — the third place
// needing the type label and capacity label, after ResourceListComponent and
// ResourceDetailComponent. Both of those had their own copy with a comment
// flagging that a third occurrence should be the one to extract; this is it.
export const RESOURCE_TYPE_LABELS: Record<ResourceType, string> = {
  Room: 'Room',
  Equipment: 'Equipment',
  Vehicle: 'Vehicle',
  LabSlot: 'Lab slot',
  Other: 'Other',
};

export function resourceTypeLabel(type: ResourceType): string {
  return RESOURCE_TYPE_LABELS[type];
}

// Derived purely from Capacity, never from ResourceType or the resource's
// name — decision `0005` makes Capacity the one axis that means exclusive vs.
// pooled, and CLAUDE.md §11 rules out inventing a rule that isn't backed by a
// field the API actually returns.
export function resourceCapacityLabel(resource: ResourceSummary | ResourceDetail): string {
  return resource.capacity === 1 ? 'Single resource' : `${resource.capacity} units`;
}
