# Admin console — tenant administration UI

**Owner-initiated, not a mentor work package.** Planned 2026-09-22, after WP-7
closed, because the owner realised the remaining work packages barely touch the
frontend and this is the largest thing still missing from the application.

---

## Status

**Planned, not started.** Phases below; per the convention every phase since
WP-3 has followed, each phase's step-by-step breakdown is written into this
document immediately before that phase starts, not drafted for all seven up
front.

| Phase | State |
|---|---|
| 1 — `GET /users` (backend only) | Not started |
| 2 — The admin shell | Not started |
| 3 — Resources: create, edit, archive | Not started |
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
| **Users** | **does not exist** | — |

**The reason codes already exist and already have throwers** (WP-3): 
`ResourceNotFound`, `InvalidTimeZone`, `CapacityBelowExistingBookings`,
`OverlappingAvailabilityWindow`, `ApproversRequired`, `ApproverNotEligible`,
`BlackoutPeriodElapsed`, `BlackoutPeriodNotFound`. So the admin console needs a
**dialect** over `booking-rejection.ts`'s existing machinery — a fifth one — and
not a new error-handling scheme.

### The one thing that has to be built on the backend

**There is no users endpoint of any kind.** `PUT /resources/{id}/approvers` takes
`approverUserIds`, and `GET /resources/{id}` returns the *currently assigned*
approvers (id and name) — so an admin can see who is assigned and has no way
whatever to discover who they could assign. The approvers screen is impossible
without it.

Settled with the owner (2026-09-22): **add `GET /users`, tenant-scoped,
TenantAdmin-only, filtered to decision `0018`'s eligible set** — own-tenant,
active, holding `Approver` or `TenantAdmin`. The narrowest endpoint that makes
the screen possible, and it reuses an eligibility rule the codebase already
enforces rather than inventing a second definition of "eligible".

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

Same discipline as WP-7: each phase is small enough to review on its own, and
its steps are written immediately before it starts.

### Phase 1 — `GET /users` (backend only)
The one backend addition. Tenant-scoped, TenantAdmin-only, filtered to decision
`0018`'s eligible set, paged like every other list (decision `0015`). Reviewable
entirely on its own, before any frontend depends on it — the shape WP-7 Phase 6
step 1 proved worth taking.

### Phase 2 — The admin shell
`isTenantAdmin` on `AuthService` (only `canApproveBookings` exists today), an
`adminGuard` mirroring `approverGuard`, the admin routes, a nav section that
appears for a TenantAdmin only, and the admin rejection dialect over
`booking-rejection.ts`'s machinery. No management screens yet — this is the
scaffolding, and it is where the role plumbing is proven.

### Phase 3 — Resources: create, edit, archive
The create and edit forms, with §4.5's three refusals rendered in place, and
archive behind its hard confirmation. Settles §4.3's open question about how
creation handles the approvals flag.

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
- **`GET /resources` has no `isArchived`-only filter** — it has `includeArchived`,
  so an "archived resources" view would have to filter client-side or the
  parameter would need widening. Check during phase 3 rather than assuming.
- **User management stays out of scope** (§3) and has no backend whatever.

## 7. Screens to design

None of these has a provided design, and the outstanding design pass already
covers four member-facing screens. Worth deciding early whether this console
waits for designs or follows the app's existing card vocabulary as the approval
queue did.

- Admin resource list (or an admin mode on the existing one — a phase-2 call)
- Resource create / edit form
- Availability windows editor
- Approvers picker
- Blackout periods list and form
