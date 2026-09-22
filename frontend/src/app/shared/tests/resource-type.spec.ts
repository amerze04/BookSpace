import { ResourceDetail, ResourceSummary } from '../../features/resources/models/resources.models';
import {
  RESOURCE_TYPE_LABELS,
  resourceCapacityLabel,
  resourceTypeLabel,
} from '../resource-type/resource-type';

// WP-7 Phase 7 step 2. Two pure functions producing **user-visible copy**, used
// by three screens — the resource list, the resource detail and availability.
// Small enough to look not worth testing, which is exactly why the capacity
// label is worth pinning: it encodes decision `0005`, and getting it wrong is a
// silent misstatement about what a resource *is* rather than a crash.

function resource(capacity: number): ResourceSummary {
  return {
    id: 'r1',
    name: 'Conference Room A',
    description: null,
    resourceType: 'Room',
    capacity,
    timeZoneId: 'UTC',
    requiresApproval: false,
    isArchived: false,
  } as ResourceSummary;
}

describe('resourceTypeLabel', () => {
  // The one label that is not simply its enum name. A `ResourceType` added
  // backend-side without a label here would be a compile error rather than a
  // blank on screen, because the record is typed over the full union — this
  // test is what notices if that typing is ever loosened.
  it('spaces LabSlot into something a person would write', () => {
    expect(resourceTypeLabel('LabSlot')).toBe('Lab slot');
  });

  it('passes the rest through as their own names', () => {
    expect(resourceTypeLabel('Room')).toBe('Room');
    expect(resourceTypeLabel('Equipment')).toBe('Equipment');
    expect(resourceTypeLabel('Vehicle')).toBe('Vehicle');
    expect(resourceTypeLabel('Other')).toBe('Other');
  });

  it('has a label for every type it claims to cover, none of them blank', () => {
    for (const [type, label] of Object.entries(RESOURCE_TYPE_LABELS)) {
      expect(label.trim(), `${type} has no label`).not.toBe('');
    }
  });
});

describe('resourceCapacityLabel', () => {
  // **Decision `0005`: `Capacity = 1` *is* exclusivity**, and `ResourceType`
  // never constrains it. So the label is derived from capacity alone — a room
  // of capacity 1 and a printer of capacity 1 read the same, deliberately.
  it('calls a capacity of one a single resource, whatever its type', () => {
    expect(resourceCapacityLabel(resource(1))).toBe('Single resource');
    expect(resourceCapacityLabel({ ...resource(1), resourceType: 'Equipment' } as ResourceSummary)).toBe(
      'Single resource',
    );
  });

  it('counts units above one', () => {
    expect(resourceCapacityLabel(resource(2))).toBe('2 units');
    expect(resourceCapacityLabel(resource(12))).toBe('12 units');
  });

  // Takes either shape, because the list renders summaries and the detail
  // screen renders the fuller record — one function rather than two that could
  // word the same fact differently.
  it('reads a detail record as happily as a summary', () => {
    const detail = {
      ...resource(3),
      minDurationMinutes: null,
      maxDurationMinutes: null,
      availabilityWindows: [],
      approvers: [],
    } as unknown as ResourceDetail;

    expect(resourceCapacityLabel(detail)).toBe('3 units');
  });
});
