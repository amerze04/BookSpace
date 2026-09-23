# Admin console — tenant administration UI

**Owner-initiated, not a mentor work package.** Planned 2026-09-22, after WP-7
closed, because the owner realised the remaining work packages barely touch the
frontend and this is the largest thing still missing from the application.

---

## Status

**In progress.** Phases 1–3 are done; phases 4–7 below. The owner asked on
2026-09-22 that each phase be built in one go rather than split into separately
reviewable steps, on the judgment that they are individually small enough —
so unlike WP-7, there is no per-phase step breakdown written ahead of the work.

| Phase | State |
|---|---|
| 1 — `GET /users` (backend only) | **Done 2026-09-22** |
| 2 — The admin shell | **Done 2026-09-23** |
| 3 — Resources: create, edit, archive | **Done 2026-09-23** |
| 4 — Availability windows editor | Not started |
| 5 — Approvers editor | Not started |
| 6 — Blackout periods | Not started |
| 7 — Wiring, click-through, close | Not started |

---

## 1. Why this exists, and what it is not

**It is not new scope discovered late.** The gap has been written down as a gap
since WP-7 Phase 1 — `docs/wp7-plan.md` §7 ("Flagged gap, not silently dropped:
resource admin CRUD… the designs already assume it and the backend has supported
all of it since WP-3") and `STATE-OF-THE-APP.md` §4 say the same thing. Every
work package's task list has been member-facing, so the buttons were left absent
rather than shown disabled, and flagged each time.

**It is not a phase of WP-7, and not a WP-8.** WP-7 is closed, and CLAUDE.md §12
says the build roadmap "mirrors the work packages exactly" and is "not an
independently-invented build order" — so inventing a work package the mentor
never issued would break the rule that keeps that file defensible. This follows
the precedent already in §12 for owner-initiated work: the **hardening pass of
2026-09-15** and the **resource list filters** entry, both of which are their own
named sections rather than pretending to be packages. This gets the same
treatment, plus its own plan document because it is roughly WP-7-sized.

**Who it is for:** a `TenantAdmin`. Every write endpoint below is behind
`AuthorizationPolicies.TenantAdmin`, so an Approver or a Member has no business
on these screens and will be refused by the server regardless of what the UI
shows.

---

## 2. The contract it builds on — audited 2026-09-22

Read off the controllers, not assumed. **This is the audit WP-7 Phase 6 did not
do and paid for**, when a screen shipped linking into a 404.

| Area | Endpoint | Policy |
|---|---|---|
| Resources | `POST /resources` | TenantAdmin |
| | `PUT /resources/{id}` | TenantAdmin |
| | `POST /resources/{id}/archive` | TenantAdmin |
| | `GET /resources`, `GET /resources/{id}` | TenantMember |
| Availability | `PUT /resources/{id}/availability-windows` | TenantAdmin |
| Approvers | `PUT /resources/{id}/approvers` | TenantAdmin |
| Blackouts | `POST /resources/{id}/blackout-periods` | TenantAdmin |
| | `PUT /resources/{id}/blackout-periods/{id}` | TenantAdmin |
| | `DELETE /resources/{id}/blackout-periods/{id}` | TenantAdmin |
| | `GET /resources/{id}/blackout-periods` | TenantMember |
| **Users** | `GET /users` — **added by phase 1** | TenantAdmin |

**The reason codes already exist and already have throwers** (WP-3): 
`ResourceNotFound`, `InvalidTimeZone`, `CapacityBelowExistingBookings`,
`OverlappingAvailabilityWindow`, `ApproversRequired`, `ApproverNotEligible`,
`BlackoutPeriodElapsed`, `BlackoutPeriodNotFound`. So the admin console needs a
**dialect** over `booking-rejection.ts`'s existing machinery — a fifth one — and
not a new error-handling scheme.

### The one thing that had to be built on the backend — **built, phase 1**

**There was no users endpoint of any kind.** `PUT /resources/{id}/approvers` takes
`approverUserIds`, and `GET /resources/{id}` returns the *currently assigned*
approvers (id and name) — so an admin can see who is assigned and has no way
whatever to discover who they could assign. The approvers screen is impossible
without it.

Settled with the owner (2026-09-22): **add `GET /users`, tenant-scoped,
TenantAdmin-only, filtered to decision `0018`'s eligible set** — own-tenant,
active, holding `Approver` or `TenantAdmin`. The narrowest endpoint that makes
the screen possible, and it reuses an eligibility rule the codebase already
enforces rather than inventing a second definition of "eligible".

Built 2026-09-22 as `GET /users`, exactly that shape. It returns `id`,
`fullName`, `email` and `roles`. Email is on the wire here and deliberately is
**not** on `ApproverSummary`: that one answers "who approves this room" for every
member of the tenant, where an address is contact information nobody asked for,
while this one is TenantAdmin-only and its job is telling two people with the
same name apart. Roles are there because `0018` leaves the picker nothing else
true to say about eligibility.

---

## 3. Settled before planning (2026-09-22)

- **Scope is resources, availability windows, approvers and blackout periods** —
  exactly what the backend already supports, plus the one users endpoint.
  **User management is out**: inviting, deactivating and role assignment have no
  backend at all, and would be a substantial backend package before any UI.
- **`GET /users` is eligibility-filtered**, as above.
- **Archive stays irreversible, and the UI exposes it anyway**, behind a hard
  confirmation. There is no unarchive and `ResourcesController` explains why:
  nothing in this system is deleted, DELETE is deliberately unimplemented, and
  the PRD never asked for a way back. The confirmation must say plainly that it
  cannot be undone — and that existing bookings are *not* cancelled by it, which
  is the thing an admin will actually worry about.
- **Structure**: its own section in CLAUDE.md §12 as owner-initiated work, not a
  work package, per §1 above.

---

## 4. Five design problems worth knowing before the first line

These are what make this more than CRUD forms. Each is forced by the contract or
by an existing decision, not by preference.

### 4.1 Two interaction models, chosen by the API rather than by us

- **Availability windows and approvers are `PUT` replace-the-set.** The whole
  collection goes in one request. So those screens are "edit everything, save
  once" — a form with a dirty state, not a list with inline add and delete.
- **Blackout periods are genuine per-row CRUD**, and `DELETE` is a real hard
  delete (decision `0019`, the one place in the system where something is
  actually removed).

Three screens, two shapes. Making them look alike would mean lying about one of
them.

### 4.2 Replace-the-set has no concurrency protection on the wire

`Resources` gained a `RowVersion` in the 2026-09-15 hardening pass (decision
`0023`'s amendment) — but **no Resources DTO carries it**, checked 2026-09-22.
The version guards the server's own read-check-write across the two endpoints
that share the FR-3.3 invariant; it is not something a client can participate in.

Consequence: two admins editing one resource's windows will silently
last-write-wins, and the loser gets no warning. That is how the API is today, it
is tolerable for a small admin team, and closing it means putting a version on
the wire — a backend change. **Flagged in §6, not folded in.**

### 4.3 `RequiresApproval ⇒ approvers exist` spans two endpoints

FR-3.3, enforced by `ResourceWriteRules` and thrown as `ApproversRequired`. A
resource cannot have `RequiresApproval = true` with an empty approver list, and
the flag and the list are set by **different requests**.

So the order matters, and the UI has to know it: approvers first, then the flag.
(The integration-test helper does exactly this, and a probe of mine failed until
it did.) The open question is how the *create* form handles it — collect
approvers as part of creation, or create unapproved and require a second step
before the flag can be turned on. **A phase-3 decision, not settled here.**

### 4.4 Creating a blackout cancels live bookings

Decision `0001` gives a blackout absolute priority, and the cascade is
forwards-only (`0019`). So an admin creating a blackout over a busy afternoon
will cancel other people's bookings, and `Booking.CancelForBlackout` leaves the
actor null precisely because no person did it individually.

The confirmation therefore has to be more than "are you sure": it should say what
will be cancelled. Whether it *can* is an open question — the cancellation
happens inside the POST, so showing it in advance means querying bookings in the
window first and accepting that the answer may be stale by a moment. **A phase-6
decision.**

### 4.5 The create/edit form has three rules that refuse it

- **Capacity cannot drop below existing bookings** —
  `CapacityBelowExistingBookings`, which counts `Pending` as well as `Confirmed`
  (established during WP-7 Phase 6 step 6).
- **`TimeZoneId` must be a canonical IANA id** — `InvalidTimeZone`. Not merely
  resolvable: CLAUDE.md §4.3 requires `TryConvertIanaIdToWindowsId` to succeed,
  which rejects `"Eastern Standard Time"` and `"america/new_york"` deliberately.
  A picker sourced from `Intl.supportedValuesOf('timeZone')` gives canonical IANA
  ids and is the right source.
- **Availability windows may not overlap** — `OverlappingAvailabilityWindow` —
  and `ClosesAt = 23:59:59` unconditionally means the following midnight
  (decision `0022`). The editor has to encode both, and the midnight convention
  is the sort of thing that looks like an off-by-one bug if it is not deliberate.

---

## 5. Phasing

Each phase is small enough to review on its own. Unlike WP-7 they are **not**
split into separately reviewable steps — the owner's call on 2026-09-22 — so a
phase is built, tested and reported in one go.

### Phase 1 — `GET /users` (backend only) — **Done 2026-09-22**
The one backend addition. Tenant-scoped, TenantAdmin-only, filtered to decision
`0018`'s eligible set, paged like every other list (decision `0015`). Reviewable
entirely on its own, before any frontend depends on it — the shape WP-7 Phase 6
step 1 proved worth taking.

Three things are true now that were not when this was planned, and phase 5's
approvers picker is built against them:

- **The route is broader than the answer, deliberately.** `GET /users` returns
  the eligible-approver set, not the tenant's users, and there is no parameter
  that would widen it. Said plainly in `ListUsersQueryRequest`'s own header so
  nobody later reads the absence of a Member as a bug.
- **The eligibility rule moved out of memory and into SQL.** It used to run in
  C# over an already-fetched candidate list (`FindEligibleApproverIdsAsync`), on
  the argument that an `EF.Property` expression over the private
  `_roleAssignments` backing field is fragile and unreadable. That trade stops
  working for a *paged* answer: filtering after `OFFSET`/`FETCH` pages over the
  wrong set and reports a `TotalCount` that counts ineligible people. The
  predicate is now one `Expression<Func<User, bool>>` in `UserRepository`, used
  by both methods — because two copies could disagree, and the way they would
  disagree is the worst available: an admin offered somebody the write path then
  refuses, with `0018` collapsing every reason into `ApproverNotEligible` so the
  screen cannot say why.
- **Both authorization policies are stacked on the controller**, unlike
  `ResourcesController` where the class policy is the weaker one. `TenantAdmin`
  admits SysAdmin by role, and a SysAdmin has no `orgId` claim, so on that policy
  alone this endpoint would answer them `200` with an empty page — a confusing
  way to say 403. `TenantMember` alongside it is what makes the refusal explicit.
  Verified live: member `403`, approver `403`, anonymous `401`.

No new reason code, no migration, no change to any existing endpoint's contract.

### Phase 2 — The admin shell — **Done 2026-09-23**
`isTenantAdmin` on `AuthService`, an `adminGuard` mirroring `approverGuard`, the
admin routes, a nav item that appears for a TenantAdmin only, and the admin
rejection dialect. No management screens yet — this was the scaffolding, and it
is where the role plumbing got proven.

**§7's open question is settled: the console follows the app's existing card
vocabulary**, as the approval queue did, rather than waiting for a design pass.
Nothing else in the app is blocked on that pass, and a console built to the
established vocabulary can be restyled later; a console not built at all cannot.

**The phase-2 call §4.4 left open is settled too: the console is its own route
tree at `/admin`, not an "admin mode" on `/resources`.** Three reasons, in order
of weight:

- The two lists answer different questions. `/resources` is "find something to
  book" and hides archived rows by design (FR-3.5); the admin list is "manage
  the catalogue" and has to show them.
- WP-7's booking detail screen is the cautionary tale for the alternative. One
  screen serving two audiences needed `viewerIsOwner` threaded through every
  string, and it still shipped telling an approver "the time is not held for
  *you* yet" about somebody else's request.
- A separate tree lets `adminGuard` protect the console once, on the parent, so
  every screen phases 3–6 add is covered without remembering to ask.

Four things are true now that were not, and phases 3–6 are built on them:

- **`isTenantAdmin` admits `TenantAdmin` and nothing else — SysAdmin is
  deliberately excluded**, even though the backend's own
  `AuthorizationPolicies.TenantAdmin` admits them by role. Every admin endpoint
  also carries `TenantMember`, which requires the `orgId` claim a SysAdmin does
  not have, so a SysAdmin admitted to the console would meet a 403 on every
  request in it. The UI matches the *effective* permission, not the policy name.
- **The rejection machinery moved to `core/http/rejection.ts` and became generic
  over its field-name type.** It was never booking-specific — it reads a
  ProblemDetails and maps a reason code onto copy — and the admin forms need it
  over an entirely different vocabulary of controls. `booking-rejection.ts` is
  now the one-off dialect plus the booking field union, and re-exports
  `RejectionDialect`/`RejectionCopy` already bound to that union so its three
  sibling dialects are untouched. Their specs are the proof the move changed no
  behaviour.
- **`resource-rejection.ts` is the fifth dialect**, covering all three resource
  writes (`POST /resources`, `PUT /resources/{id}`,
  `POST /resources/{id}/archive`) because they share a vocabulary and an
  audience. Two of its messages carry information no other screen has: that
  `CapacityBelowExistingBookings` counts Pending requests (decision `0005` — a
  pending booking reserves its units in full), and that `ApproversRequired` is
  fixed on a *different* screen, since the flag and the approver list are set by
  two different endpoints (§4.3).
- **`ConcurrencyConflict` is reachable on `PUT /resources/{id}`** and is handled.
  `Resources` has carried a `RowVersion` since the 2026-09-15 hardening pass, so
  two admins saving the same resource at once is caught rather than silently
  last-write-wins, and the copy says to reload rather than re-send. This does
  **not** close §4.2 — that is about the replace-the-set child collections, where
  no version reaches the wire at all.

`/admin/resources` renders the placeholder component for now, exactly as
`/approvals` did from WP-6 until WP-7 Phase 6 replaced it. Phase 3 replaces it.

### Phase 3 — Resources: create, edit, archive — **Done 2026-09-23**
The admin resource list at `/admin/resources`, one form serving both
`/admin/resources/new` and `/admin/resources/:id`, and archive behind its hard
confirmation. §4.5's three refusals are rendered in place.

**§4.3 is settled, and the answer turned out to be structural rather than a
matter of taste.** A resource that does not exist cannot have approvers, and
approvers are assigned by a different endpoint — so `requiresApproval` can only
ever be false at creation. Verified against the live API rather than assumed:
`POST /resources` with `"requiresApproval": true` answers **422
`ApproversRequired`**. The control is therefore rendered *disabled* rather than
hidden, with the reason beside it, and opens up on the edit form as soon as
`ResourceDetail.approvers` is non-empty. Hidden would have been worse: an
administrator looking for the setting should find it and learn when it becomes
available, not wonder where it went.

**Consequence worth stating plainly: between now and phase 5, no resource can
be made approval-gated through the UI at all.** Nothing links to the approvers
screen either, because it does not exist yet and WP-7 Phase 6 already taught
this project what linking into a 404 costs. That is a phase boundary, not a gap.

**§6's flagged item is checked, and the parameter is adequate.** `GET /resources`
offers `includeArchived` (a widening) and nothing that narrows *to* archived. The
list therefore has an "Include archived" toggle mapped straight onto it and no
archived-only view: filtering a fetched page client-side would leave `totalCount`
and the page boundaries describing the unfiltered set, which is phase 1's lesson
in a different place.

Other things true now:

- **One component for create and edit.** Every field, every refusal and the
  §4.5 rules are shared; the differences are a heading, a CTA, whether an id is
  loaded first, and two sections that exist in edit mode alone. Two components
  would be two copies of the field vocabulary, and the first to drift would be
  the one nobody was looking at.
- **Archive is on the form, not on a list row.** It cannot be undone, so the one
  thing worth buying is that the administrator is looking at the resource when
  they decide. The confirmation says both of the things an admin actually worries
  about — that there is no way back, *and* that existing bookings are **not**
  cancelled (confirmed in `Resource.Archive`, which flips a flag and nothing
  else). An acknowledgement tick rather than type-the-name: nothing is deleted
  and no booking is cancelled, so type-to-confirm would be friction out of
  proportion to the act.
- **The form goes read-only once archived**, both for a resource archived here
  and one that arrived that way, because `PUT /resources/{id}` answers 422
  `ResourceArchived` (verified live). The list reads such a row's action as
  "View" rather than "Edit".
- **`TimeZoneChangeNotice` is surfaced.** Changing the timezone *reinterprets*
  every availability window rather than shifting it (decision `0003`), and the
  server reports it because it is surprising. The form renders the count.
- **The timezone picker is `Intl.supportedValuesOf('timeZone')`** — canonical
  IANA ids from the host's own ICU data, because CLAUDE.md §4.3 refuses a
  resolvable-but-non-canonical id. **It omits `"UTC"`**, which a test caught:
  UTC is a tz database *link*, not a zone. It is added explicitly, and the
  backend accepting it was verified against the running API rather than assumed.
  `InvalidTimeZone` stays handled anyway — the two ICU catalogues can disagree
  at the edges, so narrowing the input makes that refusal rare, not impossible.

**Two bugs found while building, neither in phase 3's own code:**

- **The shell's breadcrumb crashed on a repeated crumb.** It tracked by the
  crumb's own text, and Angular throws NG0955 on a duplicate track key — taking
  the whole shell down, not just the breadcrumb. Now tracked by position, which
  is the only honest key for a list of plain strings.
- **Route `data` inherits further than it looks.** Angular's default
  `paramsInheritanceStrategy` ('emptyOnly') copies a parent's `data` onto any
  child with an empty path *or no component*, so a componentless `resources`
  grouping route under `admin` inherited `title: 'Admin'` and the breadcrumb read
  "Admin > Admin > Resources". The admin routes are flat siblings for that
  reason. The member-facing `resources` group has the same shape and gets away
  with it only because its parent carries no title to inherit.

### Phase 4 — Availability windows editor
The weekly editor, replace-the-set, encoding decision `0022`'s midnight
convention and refusing overlaps before the server has to.

### Phase 5 — Approvers editor
Replace-the-set against phase 1's endpoint. `ApproverNotEligible` collapses every
ineligibility reason into one code (decision `0018`), so the UI cannot explain
*why* someone is ineligible — which is an argument for the picker only ever
offering eligible people in the first place.

### Phase 6 — Blackout periods
Per-row CRUD, the one screen with a real delete. Settles §4.4's question about
what the cancellation confirmation can show.

### Phase 7 — Wiring, click-through, close
No new screens. A navigation-chain spec for the admin paths in the shape WP-7
Phase 7 step 1 established, a click-through script for an admin — WP-7's history
says that is where the bugs are — and the write-up.

---

## 6. Flagged, not folded in

- **No concurrency protection on replace-the-set** (§4.2). Closing it needs a
  version on the wire, which is a backend change nobody has asked for.
- **No unarchive** (§3). Settled as acceptable; if it is ever wanted it needs its
  own decision record, because `ResourcesController` currently argues against it
  in writing.
- **`GET /resources` has no `isArchived`-only filter** — **checked in phase 3,
  and the parameter is adequate.** It offers `includeArchived` (a widening) and
  nothing that narrows *to* archived, so the admin list has a toggle mapped onto
  that and no archived-only view. Filtering a fetched page client-side would
  leave `totalCount` and the page boundaries describing a different set than the
  rows under them. Not worth a backend change.
- **Approval cannot be turned on through the UI until phase 5.** FR-3.3 needs
  approvers first and the approvers screen does not exist yet; nothing links to
  it, deliberately. A phase boundary, not a gap — see Phase 3 above.

## 7. Screens to design — **settled, phase 2**

None of these had a provided design, and the outstanding design pass already
covers four member-facing screens. **Settled 2026-09-23: this console follows
the app's existing card vocabulary rather than waiting**, exactly as the
approval queue did. Nothing else is blocked on that design pass, and a console
built to the established vocabulary can be restyled later; one not built at all
cannot. Each screen below is still worth a look whenever the pass happens.

- Admin resource list — **a separate screen at `/admin/resources`**, not a mode
  on the member-facing one. Settled in phase 2; reasoning in the Phase 2
  section above.
- Resource create / edit form
- Availability windows editor
- Approvers picker
- Blackout periods list and form
