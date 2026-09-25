# Admin console — tenant administration UI

**Owner-initiated, not a mentor work package.** Planned 2026-09-22, after WP-7
closed, because the owner realised the remaining work packages barely touch the
frontend and this is the largest thing still missing from the application.

---

## Status

**Done 2026-09-23.** All seven phases are built, and the owner walked
[`admin-clickthrough.md`](admin-clickthrough.md) end to end on 2026-09-23 —
paths A to E, everything passing, no defects reported. That walk is what closes
this, not the test suite and not the API probes: the claim is that an
administrator can run their tenant without being misled, and no amount of
request/response evidence converts into it. Same rule WP-7 applied to its first
acceptance criterion.

The owner asked on 2026-09-22 that each phase be built in one go rather than
split into separately reviewable steps, on the judgment that they are
individually small enough — so unlike WP-7, there is no per-phase step breakdown
written ahead of the work.

| Phase | State |
|---|---|
| 1 — `GET /users` (backend only) | **Done 2026-09-22** |
| 2 — The admin shell | **Done 2026-09-23** |
| 3 — Resources: create, edit, archive | **Done 2026-09-23** |
| 4 — Availability windows editor | **Done 2026-09-23** |
| 5 — Approvers editor | **Done 2026-09-23** |
| 6 — Blackout periods | **Done 2026-09-23** |
| 7 — Wiring, click-through, close | **Done 2026-09-23** |

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
  **It became its own package on 2026-09-23** —
  [`user-management-plan.md`](user-management-plan.md), planned the day this
  one closed. The judgment held: it turned out to need the first email path in
  the application, an activation-token table and three new decision records.
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

### Phase 4 — Availability windows editor — **Done 2026-09-23**
The weekly editor at `/admin/resources/:id/availability-windows`,
replace-the-set, encoding decision `0022`'s midnight convention as an "Until
midnight" control rather than a magic time, and refusing overlaps client-side
before the server has to.

**Adjacency is not overlap**, on both sides: `ClosesAt` is exclusive, so
09:00-12:00 and 12:00-17:00 coexist. The client rule restates the server's
exactly, and being stricter than the API it writes to would be a bug — verified
live, the endpoint accepts that pair and answers 409 for a genuine overlap.

The rules are pure functions in `windows/window-editor.ts`; an empty schedule is
a real saveable state ("closed"); and `availability-rejection.ts` is the sixth
dialect and the only one that offers a retry, because replace-the-set is
idempotent by construction.

### Phase 5 — Approvers editor — **Done 2026-09-23**
`/admin/resources/:id/approvers`, replace-the-set against phase 1's `GET /users`.
FR-3.3, and the first screen phase 1's endpoint actually has a caller for.

- **The picker only ever offers eligible people, and that is forced rather than
  polite.** Decision `0018` collapses every ineligibility reason into one
  `ApproverNotEligible` code — deliberately, because naming the cause would
  confirm a cross-tenant id exists somewhere (AC-4). So a picker that let an
  admin type an id could only answer "no" without saying why. Offering the
  eligible set is the only shape that can explain itself.

- **The screen reconciles two endpoints that do not agree**, and the gap is real
  rather than theoretical. `FindApproverSummariesAsync` does **not** filter by
  `IsActive` — checked against the repository — so somebody assigned and later
  deactivated still comes back on `GET /resources/{id}`. They will *not* come
  back from `GET /users`, which applies `0018`'s active requirement.

  A picker built the obvious way — render the eligible, tick the assigned —
  would never show them, and **the very next save would silently drop them**.
  They cannot be kept either: re-sending the id is refused, because the server
  checks the whole requested set. So they are rendered as a separate "no longer
  able to approve" group, with the plain statement that saving removes them.
  `strandedApprovers` is that computation, and it has its own tests precisely
  because it is empty in every healthy tenant and nothing would exercise it by
  accident.

- **"Stranded" is only trusted when the picker is showing everyone** — no search
  term and one page. A searched picker is showing a subset, so absence proves
  nothing, and somebody merely on another page is not stranded.

- **A selection survives a search.** The picker reloads on every search but the
  selection is held as ids in their own signal, never as flags on the option
  objects, which are replaced wholesale each time. Losing a tick because
  somebody scrolled out of view would be the same silent removal again.

- **`approver-rejection.ts` is the seventh dialect.** Its `ApproverNotEligible`
  copy deliberately names neither the person nor the reason — `0018` forbids the
  second and the code does not carry the first — and says what is both true and
  useful instead: something changed since the page loaded, reload to see what.

- **Decision `0028`'s loose end is closed.** The resource form's "no approvers
  assigned" warning now links here; it deliberately pointed nowhere in phases 3
  and 4, because the screen did not exist and WP-7 Phase 6 already taught this
  project what linking into a 404 costs. The approvers screen carries the same
  warning from the other side: emptying the list on a gated resource is allowed,
  and says the requests will go to the tenant's administrators.

### Phase 6 — Blackout periods — **Done 2026-09-23**
`/admin/resources/:id/blackout-periods`. FR-3.4, decisions `0001` and `0019`.
The last screen, and the only one that is genuinely per-row CRUD with a real
hard delete — §4.1 said making it look like the replace-the-set editors would
misrepresent it, and it does not.

**§4.4 is settled, and the answer is better than the question assumed.** The
open question was whether a confirmation could show which bookings a blackout
will cancel, given the cascade happens inside the POST. Two things found by
reading the backend rather than guessing:

- **The response already reports exactly what was cancelled** —
  `CancelledBookingSummary`, on both the create and the edit bodies, carrying the
  booking's owner, its interval and its `recurrenceRuleId`.
- **A pre-flight preview is obtainable and accurate.** `GET /bookings` takes
  `resourceId`, and its `from`/`to` are an **overlap** filter
  (`EndsAtUtc > from && StartsAtUtc < to`) — the identical predicate
  `FindBookingsToCancelAsync` uses.

So the screen does both, and is careful about which is which:

- **Before** — an opt-in preview ("Check what this would cancel"), from a real
  query, filtered to `Pending`/`Confirmed` and not-yet-ended to match the
  cascade. Labelled as being decided at save time, because a booking created in
  between will be cancelled too. That race is documented in CLAUDE.md §6 and
  cannot be closed from a browser, so the wording promises only what it can.
- **After** — the record, from the response. Authoritative, and it names the
  occurrences that belonged to a series, because an admin seeing five should
  know they have punched holes in a series rather than ended it.

Showing only the first would be a promise the screen cannot keep; only the
second would mean an admin learns what they cancelled afterwards.

Other things true now:

- **An admin types in the resource's timezone, not their own**, matching decision
  `0003` and the confusion the WP-7 click-through surfaced. A blackout is an
  *instant*, unlike an availability window, so `blackouts/blackout-form.ts` owns
  the conversion — a two-pass inverse correcting against `utcToResourceLocal`,
  the same technique `availability-grid.ts` uses, generalized to an arbitrary
  date. Tested on both sides of a real Warsaw DST transition, and for the gap and
  ambiguity cases, which have no unique inverse and must not throw. The list
  shows both zones.
- **`BlackoutPeriodElapsed` is about the *end*, not the start.** A blackout that
  began this morning and runs through tomorrow is legal — exactly what an admin
  needs when a room floods. The copy says so, because otherwise the refusal reads
  as "you cannot black out something that has started". It is a **422**, not the
  400 its wording suggests; confirmed against the running API.
- **The delete confirmation's load-bearing sentence is that deleting is not an
  undo.** Decision `0019`'s cascade is forwards-only, so bookings a blackout
  cancelled stay cancelled and nobody is notified. An admin who assumed otherwise
  would be wrong in a way nothing else on the screen corrects.
- **Nothing on this path ever offers a retry** — the strictest of the eight
  dialects. A blackout write is not idempotent in any useful sense: repeating a
  create makes a *second* blackout, `0019` allows overlaps so nothing refuses it,
  and the first attempt may already have cancelled bookings that will never come
  back.

### Phase 7 — Wiring, click-through, close — **Done 2026-09-23**
No new screens. The seams between the six screens, a systematic coverage sweep,
a click-through script for an admin, and the write-up. 1114 vitest tests (20
new), production build clean.

**The sweep found two uncovered files and the seam tests found a real defect**,
which between them is the argument for doing phase 7 at all rather than
declaring the console done at phase 6.

#### What the coverage sweep found

Files were **enumerated, not scanned by name** — WP-7 Phase 7's lesson, learned
there when an eyeball audit found two holes and the enumeration found five.
Two admin files had shipped with no spec of their own:

- **`rejection/approver-rejection.ts`** (phase 5). The seventh dialect, and the
  only one of the eight without tests — missed because its name sits in the
  middle of six covered siblings. Now has ten, and two of them are about a
  security property rather than about copy: `ApproverNotEligible` must name
  neither the person nor the cause, because decision `0018` collapses all three
  causes into one code precisely so that naming one cannot confirm a
  cross-tenant id exists (AC-4).
- **`services/users.service.ts`** (phase 1). The client for the one endpoint
  this console owns outright, asserted until now only by component specs that
  mock it. Seven tests, including that an empty search term is *sent* rather
  than treated as unset — that is how the picker returns to showing everybody,
  and `strandedApprovers` refuses to trust an absence without it.

**Deliberately left uncovered, with the reasoning rather than silence:**
`core/http/rejection.ts` has no spec of its own and does not need one — all nine
of its branches are exercised through the eight dialects, including the two odd
ones (a non-`HttpErrorResponse` input and a 4xx whose body is not a
ProblemDetails, both covered by `booking-rejection.spec.ts`). The rest of the
sweep's list is wire-type modules with no runtime behaviour, plus
`paged-result.ts`, `skip-error-toast.ts` and two presentational components.

#### The defect the seam tests found

**All three per-resource editors had a return leg that no test could follow.**
Each one's "Back to resource" was a `<button (click)="goToResource()">`, which
works when clicked and fails at everything else: it cannot be ctrl-clicked or
opened in a new tab, and `navigation-chain.spec.ts` cannot follow it, because
that file's whole discipline is reading the `href` the previous screen rendered.
Nothing in the suite said so — each editor's own spec is about the component,
and no component spec is about where the next screen is.

They are real anchors now. Two details worth keeping:

- **The in-flight guard survives as a shape change, not as a class.** An anchor
  cannot be disabled, so while a save is in flight the availability-windows and
  approvers editors render a disabled `<button>` instead, and swap back when it
  settles. Leaving mid-save means never learning whether it landed, which is the
  rule WP-7 Phase 3 established for every control on a submitting form. The
  blackouts footer has no such guard and needs none: a blackout create or edit
  happens in its own inline form.
- **The archived branches now point at the resource too**, where they used to
  send an admin all the way out to the list. An archived resource still exists
  and its form still renders read-only, so the list is two hops further than the
  place they came from. The **not-found** branches still go to the list, because
  there the resource genuinely is not there.

`goToResource()` and the `Router` injection that existed only for it are gone
from all three components.

#### What the navigation chain covers now

Three tests added to `app/tests/navigation-chain.spec.ts`, in the shape WP-7
Phase 7 step 1 established — every hop follows a rendered `href`, never a URL
the test built:

- **The loop, three times**: resource → editor → resource, for each of the three
  editors. Walking the loop rather than each hop separately is what makes it an
  assertion about *where* the link goes: a return leg pointing at the list would
  pass a "does it navigate" test while costing two extra hops on every use.
- **Decision `0028`'s loose end as a seam** — the gated-without-approvers
  warning's "Assign approvers" link is followed onto a screen that loads.
- **An Approver bounced off all six admin URLs**, not just the entrance. The
  guard is on the parent route, and an approver is the interesting case rather
  than a member: they hold a privileged role, have their own nav item, and are
  the most likely person to try the URL.

#### Verified against the running API, 2026-09-23

Not inferred from the controllers — the numbers in the click-through come from
this probe:

- `GET /users` returns **exactly two** people for Acme (Resource Approver,
  Tenant Admin) and no Member. Default page size 20.
- `GET /resources?includeArchived=true` returns six, three archived.
- `PUT` on an archived resource → **422**; a genuinely overlapping window pair →
  **409**; a blackout that has already ended → **422**.
- Member and Approver both → **403** on `GET /users` and `POST /resources`;
  anonymous → **401**.

Two facts found in the live data are now load-bearing in the click-through,
because they exercise rules that would otherwise need to be manufactured: the 3D
Printer has adjacent `09:00–12:00` / `12:00–17:00` windows on every weekday (so
adjacency-is-not-overlap is already proven in the dataset), and the Audi A5 has
a Monday window closing at `23:59:59` (decision `0022`'s midnight convention,
live).

#### The click-through

[`admin-clickthrough.md`](admin-clickthrough.md). Five paths — the catalogue,
opening hours, approvers, blackouts, and the deliberate wrong turns — shaped by
this console's own bug history rather than by its feature list. It is a written
artefact, not a claim: **phase 7 is not closed by it, the owner's walk is.**
That is the same rule WP-7 applied to its first acceptance criterion, which was
deliberately not ticked on API evidence that had existed for four days.

Path E exists because that is where this project's bugs have lived. Three of its
items are checks on behaviour that is **known and accepted rather than correct**
— the silent last-write-wins on the replace-the-set editors (§4.2), the empty
"no longer able to approve" group that cannot be produced without a user
management backend, and an archived resource that will sit in the list forever
because there is no unarchive. Each says so on the page, so that finding them is
not mistaken for finding a bug.

#### The walk — paths A to E, 2026-09-23

**The owner walked it end to end and everything passed.** No defects reported,
no correction needed to the script, nothing deferred out of it. Phase 7 and the
admin console close here.

That is a different result from WP-7's walk, which found a real bug on its first
pass (an approver blocked from approving their own request, on a rule I had
invented and the suite was asserting). Two things are worth writing down rather
than quietly enjoying, because the difference is the useful part:

- **It is evidence about this console, not about the method.** The click-through
  earning nothing on one walk does not make the next one optional — WP-7's bugs
  were found on a first walk too, and the screens here have exactly the same
  property that produced them: what determines what you see is not what the
  assertions are about. `admin-clickthrough.md` stays a live artefact, to be
  **re-walked after any change to the admin flows**, the same way
  `wp7-clickthrough.md` is re-walked after a change to booking or approvals.
- **The three known-and-accepted items in path E were confirmed as behaving as
  written**, rather than being skipped. The silent last-write-wins on the
  replace-the-set editors (§4.2), the empty "no longer able to approve" group,
  and an archived resource that stays in the list forever are all still true,
  still flagged in §6, and are the console's honest edges rather than bugs the
  walk missed.

What this closes, precisely: **an administrator can create a resource, publish
opening hours against it, gate it for approval, staff or unstaff its approvers,
black out time on it and archive it, entirely through the UI** — and can be
refused, told why, and told what a refusal did or did not change, at every point
where the API can say no.

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
- **Approval gating no longer needs approvers at all** — decision `0028`,
  2026-09-23. This entry used to say approval could not be turned on through the
  UI until phase 5; the owner reversed the underlying FR-3.3 rule instead, on
  the grounds that it forced every gated resource through a freely-bookable
  window. Resolved, not deferred.

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
