# 0028 — A resource may require approval before it has approvers

**Decided 2026-09-23.** Owner's call, raised while reviewing admin console
phase 3. Reverses the FR-3.3 invariant this codebase had enforced since WP-3
Phase 3.

---

## The question

FR-3.3 reads "marked `RequiresApproval`, with one or more assigned approvers",
and the codebase read that as an invariant: a resource may not carry
`RequiresApproval = true` with an empty approver list. It was enforced at three
write sites (`POST /resources`, `PUT /resources/{id}`,
`PUT /resources/{id}/approvers`) through
`ResourceWriteRules.EnsureApproversWhenRequired`, and refused with
`ReasonCodes.ApproversRequired`.

Should that invariant stand?

## The answer: no

**The rule produced the state it existed to prevent.**

The flag and the approver list are set by *different* endpoints, and a resource
must exist before it can have approvers. So the only route to a gated resource
was:

1. create it — necessarily ungated, because it has no approvers yet;
2. assign approvers;
3. edit it to turn the flag on.

Between steps 1 and 3 the resource exists, appears in `GET /resources`, and is
**freely bookable**. Every booking landing in that window is confirmed outright,
with no approval and no record that one was ever intended. The invariant was
meant to guarantee "nothing is gated without someone to approve it"; what it
actually guaranteed was a window in which the resource was not gated at all.

The owner put it plainly: it makes more sense to gate the resource immediately,
and let the admin handle whatever arrives until approvers are assigned.

## Why this is safe

The objection the old rule encodes is real — an approval request nobody can
action is a booking stuck forever. It does not apply here, and three things
already in the codebase are why:

- **`ApprovalRequest` has no approver reference.** It is `BookingId`,
  `RequestedAtUtc`, `ExpiresAtUtc` and the decision. Zero approvers was always
  representable; nothing about the data model depended on the invariant.
- **`BookingApprovalReach.ResolveAsync` gives a `TenantAdmin`
  `ApprovalReach.AnyResource`** — any Pending booking in the tenant, whether or
  not the resource lists them (decision `0002`'s sweeping reach, extended to
  approvals in WP-5). So a request on a resource with no approvers is actionable
  by every tenant administrator, today, with no change.
- **A TenantAdmin is an eligible approver anyway** (decision `0018`), so this
  does not hand anyone a power they could not already have been assigned.

An unattended request is therefore not unactionable — it is in exactly the
position a request assigned to an inattentive approver is already in, and
FR-9.3's stale-approval job resolves both the same way.

## What had to change with it

**`NotificationsFor` had to stop reading the approver list directly.** Both
creation handlers built one `ApprovalRequested` notification per approver, and
`CreateBookingCommandRequestHandler`'s own comment leaned on the invariant: *"The
list cannot be empty: a resource with RequiresApproval and no approvers is
refused at both ends by ApproversRequired."*

Removing the invariant without touching that would have made the feature a trap.
A gated resource with no approvers would create Pending bookings notifying
**nobody**, and FR-9.3's expiry job would then decide them with no human ever
having been told. The admin's ability to approve would be theoretical: they would
have to think to look.

So the recipients are now **the approvers when there are any, and the tenant's
active `TenantAdmin`s when there are not** — `IUserRepository
.FindTenantAdminUserIdsAsync`. This deliberately mirrors `BookingApprovalReach`:
whoever the system says can decide is who the system tells. It is a fallback and
not an addition — an assigned approver list wins outright, so no administrator
starts receiving copies of requests that are already being handled.

## Consequences

- **`ReasonCodes.ApproversRequired` and `ApproversRequiredException` are
  deleted.** Nothing can throw the code any more, and CLAUDE.md §6 keeps the
  catalogue describing what the API can actually return — the same reason
  `ApprovalRequired` was deleted in WP-4 Phase 1a rather than left reserved. It
  is a breaking change for any client branching on it; the only client is ours,
  and its handling is removed in the same change.
- **Clearing an approver list on a gated resource is now allowed.** Previously
  the only way to remove the last approver was to un-gate the resource first,
  which turned an ordinary staffing change into a window where anyone could book
  it unapproved — the same defect as above, in a second place.
- **`ResourceWriteRules.EnsureApproversWhenRequired` is deleted.** The other
  rules in that class are unaffected.
- **The admin console's create form can offer the flag.** Admin console phase 3
  had rendered it disabled with an explanation, which was the correct reading of
  the old rule and is now unnecessary. The form instead *warns* while a gated
  resource has no approvers, naming who will be notified in the meantime.
- **`Resources.RowVersion` keeps its justification.** Decision `0023`'s
  amendment cites this invariant as the reason `Resources` needs optimistic
  concurrency. The invariant is gone, but the version is still what stops two
  admins silently overwriting each other's edits, so it stays.

## What was considered and rejected

- **Keep the rule and let the create form send approvers in the same request.**
  That means a new field on `POST /resources` and a second place where approver
  eligibility is validated. It closes the window at creation but not the one at
  step 3 of an edit, and it leaves "clear the last approver" still refused.
- **Keep the rule and create gated resources archived, un-archiving once
  approvers exist.** Archiving is one-way (FR-3.5) and means something else
  entirely. Rejected immediately.
- **Relax the rule and notify nobody.** The minimal change, and the one that
  would have made the workflow look implemented while quietly expiring requests.
  See above.
