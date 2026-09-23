import { ApproverDetail } from '../../resources/models/resources.models';
import { EligibleUser } from '../models/users.models';

// Admin console phase 5. The approvers picker's rules, as pure functions.
//
// The screen reads from **two** endpoints that do not agree with each other,
// and reconciling them is the whole job:
//
//   `GET /resources/{id}` -> who is assigned *now*, names only, **unfiltered by
//                            eligibility**
//   `GET /users`          -> who may be assigned, the decision `0018` set
//
// Those two sets are not nested. Everything in the second is assignable;
// everything in the first is assigned; and the gap between them is real, not
// theoretical — see `strandedApprovers` below.

export interface ApproverOption {
  userId: string;
  fullName: string;
  email: string;
  roles: readonly string[];
  selected: boolean;
}

// **The gap, and the bug it would otherwise cause.**
//
// `FindApproverSummariesAsync` does not filter by `IsActive` — checked against
// the repository, 2026-09-23 — so a person assigned as an approver and *later
// deactivated* still comes back on `GET /resources/{id}`. They will not come
// back from `GET /users`, because that one applies decision `0018`'s eligible
// set, which requires active.
//
// A picker built as "render the eligible users, tick the assigned ones" would
// therefore never show them, and the very next save would send a set that
// silently dropped them. The admin would have removed an approver without ever
// being asked.
//
// They cannot be kept, either: re-sending their id is refused with
// `ApproverNotEligible`, because the server checks the whole requested set
// against the same eligible query. So the only honest thing is to show them,
// say what happened, and say that saving removes them.
export interface StrandedApprover {
  userId: string;
  fullName: string;
}

// The pickable list, with the currently-assigned ones already ticked.
export function toOptions(
  eligible: readonly EligibleUser[],
  assignedUserIds: ReadonlySet<string>,
): ApproverOption[] {
  return eligible.map((user) => ({
    userId: user.id,
    fullName: user.fullName,
    email: user.email,
    roles: user.roles,
    selected: assignedUserIds.has(user.id),
  }));
}

// Assigned people the picker cannot offer, because `GET /users` no longer lists
// them. Empty in every ordinary tenant, which is exactly why it needs a test
// rather than a hope.
export function strandedApprovers(
  assigned: readonly ApproverDetail[],
  eligible: readonly EligibleUser[],
): StrandedApprover[] {
  const eligibleIds = new Set(eligible.map((user) => user.id));

  return assigned
    .filter((approver) => !eligibleIds.has(approver.userId))
    .map((approver) => ({ userId: approver.userId, fullName: approver.fullName }));
}

// What the endpoint wants: bare user ids, replace-the-set.
//
// Sorted so a request reads the same way twice for the same selection — the
// server normalizes the order in its response anyway, and an unstable payload
// order would make `hasChanges` below depend on click order rather than on the
// selection.
export function toApproverPayload(selectedUserIds: ReadonlySet<string>): string[] {
  return [...selectedUserIds].sort();
}

// Whether the selection differs from what the server last confirmed.
//
// **Compared as sets, not as arrays.** Ticking someone and unticking them again
// has to come back to "no changes", and the order people were clicked in is not
// a change at all.
export function hasChanges(
  selectedUserIds: ReadonlySet<string>,
  assigned: readonly ApproverDetail[],
): boolean {
  if (selectedUserIds.size !== assigned.length) {
    return true;
  }

  return assigned.some((approver) => !selectedUserIds.has(approver.userId));
}

// A person's roles, as a line under their name. The picker cannot explain why
// somebody is *ineligible* — decision `0018` collapses all three reasons into
// one code — so saying what makes each offered person eligible is the only true
// thing it can say on the subject.
export function roleLabel(roles: readonly string[]): string {
  const relevant = roles.filter((role) => role === 'TenantAdmin' || role === 'Approver');

  if (relevant.length === 0) {
    // Unreachable through `GET /users`, which only returns holders of one of
    // those two. Rendered as a fact rather than a blank, so an unexpected row
    // reads as odd instead of invisible.
    return 'No approving role';
  }

  return relevant.map((role) => (role === 'TenantAdmin' ? 'Administrator' : 'Approver')).join(' · ');
}
