import { ApproverDetail } from '../../resources/models/resources.models';
import { EligibleUser } from '../models/users.models';
import {
  hasChanges,
  roleLabel,
  strandedApprovers,
  toApproverPayload,
  toOptions,
} from '../approvers/approver-selection';

// Admin console phase 5. The picker's rules, tested where they live.
//
// The screen reconciles two endpoints that do not agree with each other, and
// the disagreement is not hypothetical — see `strandedApprovers` below, which
// exists because of a gap found by reading the repository rather than by
// guessing at it.

function user(overrides: Partial<EligibleUser> = {}): EligibleUser {
  return {
    id: 'u1',
    fullName: 'Resource Approver',
    email: 'approver@acme.test',
    roles: ['Approver'],
    ...overrides,
  };
}

function assigned(userId: string, fullName = 'Someone'): ApproverDetail {
  return { userId, fullName };
}

describe('toOptions', () => {
  it('ticks the people who are already assigned, and leaves the rest clear', () => {
    const options = toOptions(
      [user({ id: 'u1' }), user({ id: 'u2' })],
      new Set(['u2']),
    );

    expect(options.map((o) => [o.userId, o.selected])).toEqual([
      ['u1', false],
      ['u2', true],
    ]);
  });

  it('carries the email and roles through, which are what tell two people apart', () => {
    const [option] = toOptions([user({ email: 'a@acme.test', roles: ['TenantAdmin'] })], new Set());

    expect(option.email).toBe('a@acme.test');
    expect(option.roles).toEqual(['TenantAdmin']);
  });
});

describe('strandedApprovers', () => {
  // **The gap, and the silent removal it would otherwise cause.**
  //
  // `FindApproverSummariesAsync` does not filter by IsActive, so a deactivated
  // approver still comes back on GET /resources/{id}. GET /users applies
  // decision `0018`, which requires active, so they do not come back there.
  //
  // A picker built as "render the eligible, tick the assigned" would never show
  // them, and the next save would drop them without the admin being asked.
  it('finds an assigned person the picker cannot offer', () => {
    const stranded = strandedApprovers(
      [assigned('u1', 'Still Fine'), assigned('u9', 'Since Deactivated')],
      [user({ id: 'u1' })],
    );

    expect(stranded).toEqual([{ userId: 'u9', fullName: 'Since Deactivated' }]);
  });

  // The ordinary case, and the reason this needs a test rather than a hope: it
  // is empty in every healthy tenant, so nothing would exercise it by accident.
  it('is empty when every assigned person is still eligible', () => {
    expect(strandedApprovers([assigned('u1')], [user({ id: 'u1' })])).toEqual([]);
  });

  it('is empty when nobody is assigned at all', () => {
    expect(strandedApprovers([], [user()])).toEqual([]);
  });
});

describe('toApproverPayload', () => {
  it('sends bare ids, sorted so the same selection always reads the same', () => {
    expect(toApproverPayload(new Set(['u3', 'u1', 'u2']))).toEqual(['u1', 'u2', 'u3']);
  });

  // An empty array is a legitimate request since decision `0028` — including on
  // a gated resource, whose requests then fall back to the tenant admins.
  it('sends an empty array for an empty selection rather than omitting it', () => {
    expect(toApproverPayload(new Set())).toEqual([]);
  });
});

describe('hasChanges', () => {
  it('sees no change when the selection matches what the server confirmed', () => {
    expect(hasChanges(new Set(['u1', 'u2']), [assigned('u1'), assigned('u2')])).toBe(false);
  });

  // Compared as sets: the order people were clicked in is not a change.
  it('ignores the order the selection was built in', () => {
    expect(hasChanges(new Set(['u2', 'u1']), [assigned('u1'), assigned('u2')])).toBe(false);
  });

  it('sees an addition and a removal', () => {
    expect(hasChanges(new Set(['u1', 'u2']), [assigned('u1')])).toBe(true);
    expect(hasChanges(new Set(['u1']), [assigned('u1'), assigned('u2')])).toBe(true);
  });

  // A swap keeps the count identical, so a size-only comparison would miss it.
  it('sees a swap of one person for another', () => {
    expect(hasChanges(new Set(['u2']), [assigned('u1')])).toBe(true);
  });

  it('sees clearing the list, and sees filling an empty one', () => {
    expect(hasChanges(new Set(), [assigned('u1')])).toBe(true);
    expect(hasChanges(new Set(['u1']), [])).toBe(true);
  });
});

describe('roleLabel', () => {
  // Decision `0018` leaves the picker nothing true to say about why somebody is
  // *ineligible*, so what makes each offered person eligible is the only honest
  // thing on the subject.
  it('names the approving roles in words an administrator reads', () => {
    expect(roleLabel(['Approver'])).toBe('Approver');
    expect(roleLabel(['TenantAdmin'])).toBe('Administrator');
    expect(roleLabel(['TenantAdmin', 'Approver'])).toBe('Administrator · Approver');
  });

  // Members cannot come back from GET /users, so this is unreachable through
  // the real endpoint — rendered as a fact rather than a blank so an unexpected
  // row reads as odd instead of invisible.
  it('says so plainly rather than rendering blank for a row with no approving role', () => {
    expect(roleLabel(['Member'])).toBe('No approving role');
    expect(roleLabel([])).toBe('No approving role');
  });

  it('ignores roles that have nothing to do with approving', () => {
    expect(roleLabel(['Member', 'Approver'])).toBe('Approver');
  });
});
