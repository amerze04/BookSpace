# WP-7 — Booking UI & Calendar

Source: `docs/Work Packages - Week 5 and 6.pdf` (weeks 5–6, frontend track),
which carries WP-6 and WP-7 together. **That PDF is the authority on scope** —
the task list in CLAUDE.md §12 will be copied from it, and anything not in it
is a gap to flag rather than something to add on judgment (CLAUDE.md §11).

---

## Status

**In progress.** Plan approved by the repo owner 2026-09-15, before any code
was written, the same process WP-3 through WP-6 each went through. The owner
has allocated more than a week to this package and wants every phase built
seriously rather than rushed; each phase is split into its own smaller steps
(as WP-3 through WP-5 did on the backend, and as WP-6 did on the frontend)
once that phase is about to start, not all up front in this document.

**Phase 1 (resource list & detail) is done, 2026-09-15** — all five steps,
including the mid-phase pivot on step 3 (client-side search/approval filters
reversed to real backend query params, CLAUDE.md's "Resource list filters
extended for WP-7" entry) and its subsequent redo. 89 vitest tests pass, 0
failed. See §5 below for what each step delivered.

---

## 1. What WP-7 owes

Copied from the source doc via CLAUDE.md §12.

- Resource list and detail views.
- Availability view for a resource and date range.
- Booking form for one-off and recurring bookings, with clear validation
  feedback.
- Calendar view rendering bookings, including recurring series, without
  choking on volume.
- Approval queue UI for approvers.
- Cancellation and blackout handling in the UI.
- Wire the full flow end-to-end against the real API.

**Hard problem**: the calendar must stay responsive with hundreds of bookings
and expanded recurring series. Naive rendering will melt — think about what
you fetch and what you render.

Acceptance criteria:

- A member completes browse → book → confirm entirely through the UI.
- Recurring bookings render correctly in the calendar.
- The calendar stays responsive under realistic data volume.
- An approver can action pending requests from the UI.

WP-7 builds none of resource *administration* — create, edit, archive, or
managing availability windows/approvers/blackouts. See §7's flagged gap.

---

## 2. What already exists that WP-7 builds on

- **The authenticated shell (WP-6)** — `layout/shell/`, route guards
  (`authGuard`, `guestOnlyGuard`, `approverGuard`), the `Resources`/`My
  Bookings`/`Approvals` nav items already present (routed at a
  `features/placeholder/` page today — WP-7 gives each a real home), and
  `AuthService.canApproveBookings` for the same role gate WP-7's approval
  queue route already sits behind.
- **The error/notification stack (WP-6 Phase 4)** — `core/http/problem-details.ts`,
  `error-toast.interceptor.ts`, `skip-error-toast.ts`, and
  `core/notifications/` (`NotificationService` + `NotificationListComponent`).
  Every WP-7 form reuses the same `errors`-dictionary-to-field-message mapping
  `LoginComponent` established, not a second implementation.
- **Decoded-JWT claims are UI-only** (WP-6's standing rule): `orgId`/`role`
  drive nav visibility and which guard applies, never a substitute for what
  the backend actually enforces. WP-7 doesn't change this.
- **The full backend surface this package consumes** — all shipped and tested
  (WP-3 through WP-5, plus the 2026-09-15 hardening pass):
  - `GET /resources`, `GET /resources/{id}` — paged list + full detail,
    including `AvailabilityWindows` and `Approvers` on the detail read.
  - `GET /resources/{id}/availability?from&to&quantity` — bookable intervals
    carrying `remainingCapacity` (decision
    [`0020`](decisions/0020-bookable-interval-semantics.md)), resource-local
    dates, max 90 days, an archived resource answering `isArchived: true`
    with an empty list rather than an error.
  - `POST /bookings`, `POST /recurrence-rules` — one-off and recurring
    creation, both returning `Confirmed` or `Pending` per resource
    (`RequiresApproval`, FR-7.1), the latter reporting every occurrence as
    created/skipped/refused (FR-5.4), never a bare count.
  - `GET /bookings`, `GET /bookings/{id}`, `POST /bookings/{id}/cancel`,
    `POST /recurrence-rules/{id}/cancel` — view and cancel, own-scoped by
    default, widened by `scope=tenant` for a TenantAdmin/Approver.
  - `POST /bookings/{id}/approve`, `POST /bookings/{id}/reject` — the
    approval queue's two actions.
  - The full reason-code catalogue (`ReasonCodes`, CLAUDE.md §6) — every code
    a WP-7 screen can receive is already thrown and tested on the backend;
    this package's job is to render each one, not invent handling for a new
    one.
- **Decision [`0007`](decisions/0007-recurrence-materialization-horizon.md)
  is what makes the calendar tractable.** A recurring series is fully
  materialized at creation — every occurrence is already its own `Booking`
  row with `RecurrenceRuleId` set. The calendar therefore does **no
  client-side recurrence expansion**: it fetches bookings for a date window
  exactly like it would for one-off bookings, and a series occurrence is
  just a row that happens to carry a `RecurrenceRuleId`.
- **Decision [`0003`](decisions/0003-availability-timezone.md)** — availability
  is expressed in the *resource's* timezone, not the booker's. This governs
  Phase 2's date picker specifically; it does not extend to how an existing
  booking is displayed (see §3).
- **Decision [`0002`](decisions/0002-tenant-admin-cancellation.md)** and its
  amendment — the four cancellation mechanics (owner-filter reach, `EndsAtUtc`
  window, non-idempotent, no self-notification) Phase 4's cancel UI has to
  respect, and **[`0018`](decisions/0018-approver-eligibility.md)** — the
  eligibility set (`Approver`/`TenantAdmin`, own tenant, active) Phase 6's
  queue mirrors via `scope=tenant`.
- **No calendar/scheduling library, and none will be added** — settled below.

## 3. Settled before planning

Three questions, put to the owner on 2026-09-15 because each has a real
trade-off.

**Resource admin CRUD is out of scope for WP-7.** The two designs already
provided (`design/resources_design.png`, `design/resource_details_design.png`)
both show "New resource" and "Edit resource" — but the source PDF's task list
only says "Resource list and detail views." Owner's call: member-facing only
this package; those buttons are not rendered at all rather than shown
disabled or routed to a placeholder, since there is nothing yet for them to
do. See §7's flagged gap for where this needs to resurface.

**The calendar is a custom-built component — no new dependency.** No
calendar/scheduling library is in `package.json`, and WP-6 already set the
precedent of preferring the plain-Angular-signals answer over a library
(state management: signals + services, not NgRx) so the owner — new to
Angular — has one style to reason about, not several conventions layered by
whichever package solved a given screen. A third-party calendar would also
bring its own internal virtualization and data model, which risks fighting
the specific fetch-bounded-range strategy the hard problem is actually
about, rather than helping it. Revisit only if Phase 5 finds the custom
component genuinely can't meet the responsiveness bar — flagged again in that
phase's own section, not assumed here.

**Bookings display in the viewer's browser-local time, not the resource's
timezone.** This is a default, not a question the owner was explicitly asked
— flag it now if it should be revisited. Decision `0003` governs *querying*
availability in the resource's zone (so "Monday 9am" means what the room's
own clock says); once a booking is a concrete UTC instant, showing it
converted to the viewer's own local time is the ordinary calendar-app
convention, and neither `ListBookingsQueryResponse` nor
`GetBookingQueryResponse` carries a timezone id that would let the UI do
otherwise without an extra lookup per row.

---

## 4. Screens to design — one place, all phases

Following WP-6's pattern: the full list up front, so nothing lands mid-phase
with no design to build against.

| Phase | Screen / component | Status |
|---|---|---|
| 1 | **Resource list** | Provided — `design/resources_design.png`. Admin actions (New resource, per-card `⋮` menu) not built; see §3. |
| 1 | **Resource detail** | Provided — `design/resource_details_design.png`. "Edit resource" not built; "Check availability" routes to Phase 2. |
| 2 | **Availability view** | Needed before Phase 2 starts. |
| 3 | **Booking form** (one-off + recurring, validation states) | Needed before Phase 3 starts. |
| 4 | **My Bookings** (list, detail, cancel confirmation, series-vs-occurrence cancel choice) | Needed before Phase 4 starts. |
| 5 | **Calendar** (month/week view, event card, empty/loading/overflow states) | Needed before Phase 5 starts. |
| 6 | **Approval queue** (pending list, approve/reject with note) | Needed before Phase 6 starts. |
| 7 | *(none)* | Wiring and AC sweep only. |

---

## 5. Phasing

Seven phases, each a working increment against the real backend — no mocks,
same delivery style as every prior work package. Control returns to the owner
between phases, and — per the owner's instruction for this package
specifically — **each phase is taken seriously and split into its own
reviewable steps once it starts**, rather than compressed to move faster.

### Phase 1 — Resource list & detail (browse) — **Done** (2026-09-15)

**Superseded 2026-09-15 — reversed by the owner.** The paragraph below is
kept, struck through in spirit rather than deleted, because step 3 was
already built against it and the reversal is itself worth a record: the
first call was client-side filtering to avoid a backend change inside a
frontend package; the owner decided the opposite was worth doing properly.
`GET /resources` now has real `search` and `requiresApproval` query
parameters (backend work, done and tested 2026-09-15 — see the entry in
CLAUDE.md §12 for the full detail: `ListResourcesQueryRequest`,
`ListResourcesQueryRequestValidator`, `ResourceRepository.ListAsync`, and
`ResourcesController.ListResourcesRequest`, plus 10 new integration tests).
Frontend step 3 (search box + approval dropdown) had already been built
against the old, client-side answer in this same session and was redone
against the real endpoints the same day — see **step 3**, below, now marked
done. Switching to a real round-trip brought one new requirement: **the
search box debounces** (500ms after the last keystroke) rather than firing a
request per character, since every keystroke now costs an HTTP call instead
of a `computed()` re-filter.

<details>
<summary>Original call (2026-09-15, superseded same day)</summary>

A contract gap found while breaking this phase down: the list design has a
free-text search box and an "Approval" filter dropdown, but `GET /resources`
only ever supported `type`, `includeArchived`, and paging/sort — no search
text, no approval-required filter. Rather than add backend query params
inside a frontend package, or ship the screen visibly short of the design,
the call was: fetch one page at the API's own max (`pageSize=100`) and filter
search text + approval-required client-side, over that fetched set.

</details>

**Steps:**

1. **Models + `ResourcesService`, no UI.** TypeScript interfaces mirroring
   the backend DTOs exactly (`ResourceType`, `ResourceSummary` ≙
   `ListResourcesQueryResponse`, `ResourceDetail` ≙ `GetResourceQueryResponse`,
   `AvailabilityWindowDetail`, `ApproverDetail`, the shared `PagedResult<T>`
   envelope). `features/resources/resources.service.ts`: `list(params)` and
   `getById(id)`, both thin wrappers over `HttpClient` against
   `environment.apiBaseUrl`, no component depends on them yet.
   **Tests:** `HttpTestingController`-based spec confirming the query string
   built for each parameter combination (page/pageSize/sort/type/
   includeArchived) — the same style `auth.service.spec.ts` already
   established.

2. **Resource list — core layout + type pills.** `features/resources/list/`
   rendering the provided design's card grid from a `pageSize=100` fetch:
   icon per `ResourceType`, name, type, the capacity label, and the
   approval-required badge. Capacity label is derived **only** from
   `Capacity` — 1 → "Single resource", >1 → "`{n}` units" — not from the
   resource's name or type; the mock's "Multiple desks" for Hot Desk Area
   looks like one-off flavor text rather than a rule with anything behind it
   in the data, and inventing a per-name special case would be exactly the
   kind of undocumented rule CLAUDE.md §11 asks to avoid. The `All`/`Rooms`/
   `Equipment`/`Vehicles`/`Lab slots`/`Other` pills issue a real server
   request each (`type=...`), since that filter genuinely exists. If the
   response's `totalCount` exceeds the page actually fetched, a small notice
   says so ("Showing 100 of `{totalCount}` — narrow by type to see the
   rest.") instead of silently under-counting.
   **Deviation from the mock, not just an omission**: the card in the design
   also shows a description line, but `ListResourcesQueryResponse` doesn't
   carry one — the backend's own comment on that record is explicit that a
   list row is deliberately a summary, description included on the detail
   read only. Re-litigating that (or fetching every card's detail just for a
   snippet) is out of proportion to this step, so the description is left off
   list cards.
   **Owner's correction, applied here**: the whole card navigates to the
   resource detail route (`/resources/:id`); "Book resource" is its own link,
   `(click)`-stopping propagation so it doesn't also trigger the card's own
   navigation, to `/resources/:id/availability`. Both routes are added to
   `app.routes.ts` in this step (as children of `resources`, alongside the
   list route) rather than deferred to step 5 — a `RouterLink` needs
   somewhere to point — and both load the shared `PlaceholderComponent` for
   now: step 4 swaps in the real detail screen, Phase 2 swaps in the real
   availability screen, the same one-route-at-a-time replacement WP-6 used
   for every nav item.
   **Screens needed:** provided (`design/resources_design.png`).
   **Tests:** capacity-label and type-label formatting, the initial fetch's
   request shape, type-pill re-fetching, the truncation notice, a stale
   out-of-order response being ignored, and load-failure/retry — all as
   component-instance tests via `HttpTestingController`, not full DOM
   rendering tests.

3. **Resource list — search, approval filter, archived toggle.** ~~The
   search box and "Approval" dropdown filter the already-fetched set in the
   browser~~ — superseded same day, redone below. "More filters" holding only
   `includeArchived` was never affected by the reversal — that one was always
   a real server round-trip.

   **Done, redone against the real endpoints (2026-09-15).** Now that `search`
   and `requiresApproval` are real query params (CLAUDE.md's "Resource list
   filters extended for WP-7" entry), `ResourceListComponent` sends both as
   ordinary server round-trips, same as `selectType`/`includeArchived` —
   `filteredItems` (the client-side computed from the first attempt) is gone;
   `items` is simply whatever the last fetch returned. The search input
   **debounces 500ms** after the last keystroke
   (`Subject` → `debounceTime(500)` → `distinctUntilChanged()` →
   `takeUntilDestroyed()`, subscribed once in the constructor) before it
   triggers `load()` — the box's own displayed value (`searchText`) still
   updates on every keystroke so typing never feels laggy, only the *request*
   waits. The existing `latestRequestId` stale-response guard covers every
   trigger (type, search, approval, archived) through the same `load()`, so a
   debounced search firing after a type pill was already clicked can't
   overwrite the newer response. `isTruncated` keeps meaning what it always
   meant (more rows exist server-side than this page fetched) but is now
   truthful in one more case: a search/approval combination could itself be
   truncated at 100 rows, which the old client-side-filtering answer
   couldn't express (a search narrowed the *view*, never the *fetch*).
   **Tests:** the debounce itself (no request until the window elapses, and
   only one request — with the final text — once it does, via
   `vi.useFakeTimers()`/`vi.advanceTimersByTimeAsync`, the same technique
   `auth.service.spec.ts` already used for its own timeout), the approval
   dropdown's immediate (non-debounced) round-trip, and both combined into
   one request. One test-infrastructure gap this surfaced and fixed:
   `ResourceListComponent`'s spec had never provided `ActivatedRoute` for
   `RouterLink` (the "Book resource" link) — invisible until a test actually
   flushed Angular's zoneless auto-render (`vi.advanceTimersByTimeAsync`
   does), which none of steps 2–3's earlier tests happened to do.

4. **Resource detail — done, 2026-09-15.** `features/resources/detail/`
   matching the provided design: header (icon, name, type, approval badge,
   description), the Resource information card (type, capacity, timezone,
   status), the Booking rules card (approval / min duration / max duration /
   approvers, the last shown only when approval is required), and the
   bookable-hours table built from `AvailabilityWindows` — all seven weekdays
   always rendered, Monday first (matching the design, not the `DayOfWeek`
   enum's own Sunday-first order), "Not bookable" for a day with no window,
   multiple same-day windows joined by a comma. "Edit resource" is not
   rendered (§3). This step replaces step 2's placeholder on the `:id` route
   with the real component; "Check availability" points at the same
   `:id/availability` route step 2 already created — Phase 2 gives *that*
   route a real destination.
   **A 404 gets its own state, distinct from a generic failure**: "This
   resource doesn't exist, or you don't have access to it," with a link back
   to the list rather than a retry button (retrying the same id can't help).
   The wording deliberately doesn't distinguish "doesn't exist" from "isn't
   yours," mirroring the backend's own `ResourceNotFoundException` reasoning
   for the same AC-4 case.
   **The route param is observed, not read once**: the component subscribes
   to `ActivatedRoute.paramMap` rather than only `route.snapshot.paramMap` at
   construction, so if the router ever reuses this component instance across
   two different resource ids (navigating detail-to-detail on the same route
   config, which nothing does yet but nothing rules out either), it re-fetches
   instead of silently keeping the first resource's data on screen.
   **Also where the shell's breadcrumb gets fixed**, as flagged when step 2
   added the nesting: `ShellComponent.breadcrumb` now walks every matched
   route level's own `data.title` (`layout/breadcrumb.service.ts`'s
   `BreadcrumbService` lets a leaf page — this one — override the *last*
   crumb with something only it knows at runtime, the loaded resource's own
   name, cleared again on destroy so it can't leak onto the next page).
   `Resources > Resource details` (the static route titles) becomes
   `Resources > Conference Room A` once the resource loads.
   **A pre-existing gap fixed in passing**: `ResourcesService.list()`/
   `.getById()` never opted out of the global error toast, so steps 2–3's own
   inline error/retry UI would have shown *and* a redundant toast — fixed by
   adding `skipErrorToast()` to both calls, the same reasoning
   `AuthService.login()`/`.logout()` already apply to theirs.
   **Screens needed:** provided (`design/resource_details_design.png`).
   **Tests:** the fetch-by-id request, the success/404/other-error paths and
   `retry()`, re-fetching on a route-param change without recreation, the
   weekday-row builder (ordering, "Not bookable", multi-window joining),
   duration formatting, and the approver-name/capacity-label formatting —
   plus new coverage on `ShellComponent` (a single crumb, a multi-level
   walk, live re-derivation on `NavigationEnd`, the override replacing only
   the last crumb, and falling back once it's cleared) and a couple of
   direct `BreadcrumbService` tests, since neither had any test coverage
   before this step touched them.

5. **Final test pass + phase demo — done, 2026-09-15.** Routing was already
   in place incrementally from steps 2 and 4, so this step added no new
   code — verification only. `npx ng test --watch=false`: **13 test files,
   89 tests passed, 0 failed.** `npx ng build`: clean, no type errors.
   **No browser-automation tool was available this session**, so the
   click-through walkthrough the plan called for (log in, browse, filter,
   open a resource) was not performed and is **not claimed as done** — flagged
   explicitly rather than assumed to be fine. In its place, the real running
   backend's contract was smoke-checked directly (`curl`, authenticated as
   `member1@acme.test`) against every request shape the frontend actually
   sends: the default list, `type=Room`, `search=printer` (name match),
   `requiresApproval=true`, `search=Room&type=Equipment` (combined, correctly
   empty), `includeArchived=true` (surfaced two pre-existing archived
   resources already sitting in the dev database — "Postman Room Updated" and
   "Scratch Resource," neither created by this session — confirming the
   filter and the `isArchived` flag both work against real, not just
   fixture, data), the detail read for Conference Room A (description,
   duration limits and its five weekday windows all present and correctly
   shaped), and the 404 path (`ResourceNotFound`, matching what
   `ResourceDetailComponent.notFound` checks for). Every response matched
   what the Angular unit tests already assumed by mocking it — real evidence
   the mocks were honest, not just internally consistent. **This is not a
   substitute for an actual browser walkthrough** (rendering, the debounce
   feel, click targets, and the breadcrumb/title swap are all still
   unverified visually) — recommended before treating Phase 1 as fully
   signed off.

**Demo:** browse the seeded resources, filter by type and by search/approval
(now real server round-trips, not client-side), open one, and see its
schedule and approvers render correctly for both an approval-gated and a
plain resource.

**API:** `GET /resources`, `GET /resources/{id}`.

### Phase 2 — Availability view

- `AvailabilityService`: one method against
  `GET /resources/{id}/availability?from&to&quantity`.
- A resource-local date-range picker (the 90-day cap surfaced as a client-side
  bound before the request goes out, not only as a server rejection), a
  quantity input shown only when `Capacity > 1`.
- Renders bookable intervals with their `remainingCapacity`; an archived
  resource's empty/`isArchived` response gets its own explicit empty state,
  distinct from "closed all week."
- Selecting an interval carries the chosen span into Phase 3's booking form.

**Screens needed:** availability view (to be designed).

**Demo:** pick a range on Conference Room A (single-capacity) and on Pool
Cars (pooled) and see the different shape of answer — walls vs. a
remaining-count floor.

**API:** `GET /resources/{id}/availability`.

### Phase 3 — Booking form (one-off + recurring)

- `BookingsService.create()` / `RecurrenceRulesService.create()`.
- One form, a one-off/recurring toggle. Pre-filled from Phase 2's selected
  interval when arriving that way; also reachable directly from a resource's
  detail page for a manual date/time entry.
- Recurring fields: frequency, interval value, local start/end time, start
  date, end date **or** occurrence count (mutually exclusive, mirroring the
  command's own shape).
- Full reason-code coverage as field-level or top-of-form feedback:
  `SlotUnavailable`, `CapacityExceeded`, `OutsideAvailability`,
  `BlackoutPeriod`, `ResourceArchived`, `BookingDurationOutOfRange`,
  `BookingInThePast` (one-off); `NoOccurrencesCreated` plus the per-occurrence
  created/skipped/refused breakdown (recurring) — every code this endpoint
  can return already has a thrower on the backend, so this phase is exhaustive
  by construction, not by guessing what might come back.
- A created-but-`Pending` booking is shown distinctly from `Confirmed`
  (FR-7.1) — not just a generic success toast.
- **Idempotency-Key handling**: the client generates one GUID per submission
  *attempt* and reuses it only when retrying that same attempt after a
  transport failure (a network error, a 5xx) — never on a deliberate resubmit
  after the user changes the form. Documented in the component, not left
  implicit, since getting this backwards would either let a genuine retry
  create a second series or make an intentional second series collide with
  the first.

**Screens needed:** booking form (to be designed).

**Demo:** book a one-off slot on an approval-gated resource (see it land
Pending); book a recurring weekly series across a few weeks and see the
per-occurrence report, including at least one deliberately-forced rejection
(e.g. into an already-fully-booked slot) so the breakdown is genuinely
exercised, not just the happy path.

**API:** `POST /bookings`, `POST /recurrence-rules`.

### Phase 4 — My Bookings (view, cancel, series cancel)

- `BookingsService.list()` / `getById()` already built in Phase 3; adds
  `cancel()` and `RecurrenceRulesService.cancel()`.
- List: own bookings, paged, filterable by status/date; each row shows
  resource name, span, status, and a recurrence badge when
  `RecurrenceRuleId` is set.
- Detail: full `GetBookingQueryResponse`, including the cancellation trio
  (who/when/why) and the `Approval` section when present.
- Cancel: a single booking via `POST /bookings/{id}/cancel`; for a
  recurrence-anchored booking, an explicit choice between "cancel this
  occurrence" and "cancel the whole series"
  (`POST /recurrence-rules/{id}/cancel`) — never one button that's ambiguous
  about which it means.
- Blackout-driven cancellations render `CancellationReason` plainly (the text
  snapshot decision `0019` describes) and `CancelledByUserId` differing from
  `UserId` reads as "cancelled by an administrator," per decision `0002`.

**Screens needed:** My Bookings list/detail/cancel (to be designed).

**Demo:** cancel a one-off booking and confirm the slot frees in Phase 2's
availability view; cancel one occurrence of a series and confirm the rest
survive; cancel the whole series and confirm every future occurrence goes
with it.

**API:** `GET /bookings`, `GET /bookings/{id}`, `POST /bookings/{id}/cancel`,
`POST /recurrence-rules/{id}/cancel`.

### Phase 5 — Calendar view (the hard problem)

- A custom month/week grid — no new dependency (§3).
- **The fetch strategy is the point**: query `GET /bookings` bounded to the
  visible date window (plus `scope`), re-fetched on navigation. Never fetch
  "everything" and filter client-side.
- **No client-side recurrence expansion** — decision `0007` already
  materialized every occurrence as its own row, so a series renders exactly
  like any other set of bookings that happen to share a `RecurrenceRuleId`.
- Rendering stays cheap by only building DOM for the currently-visible range;
  a day with many bookings gets an overflow affordance ("+N more") rather
  than an unbounded stack of event chips.
- Responsiveness is checked against a realistic volume before this phase is
  called done — WP-3 Phase 5's 260-booking fixture is the existing benchmark
  to reuse or extend for this purpose.

**Screens needed:** calendar (to be designed) — including its own
loading/empty/overflow states, since those are exactly what the hard problem
makes non-trivial.

**Demo:** navigate several months on a tenant carrying a realistic booking
volume (seeded plus Phase 3/4's own test data) with no visible jank, and see
a multi-week recurring series render correctly across a month boundary.

**API:** `GET /bookings` with a date-window filter.

### Phase 6 — Approval queue UI

- `BookingsService` gains the tenant-scoped query:
  `GET /bookings?scope=tenant&status=Pending`, `approve()`, `reject()`.
- Queue list: resource name, requester, span, quantity, requested-at; an
  Approver sees only their assigned resources (`AnyOwnerRestrictedToResources`
  on the backend), a TenantAdmin sees the whole tenant's — the UI does not
  need to distinguish these cases, since the backend already scopes the
  response correctly.
- Approve/reject with an optional note; a since-taken slot's `409` (AC-5) is
  shown as a specific, distinct outcome from a generic failure.

**Screens needed:** approval queue (to be designed).

**Demo:** as an approver, action the seeded Pending 3D Printer booking; force
a second approver/admin to approve the same booking concurrently and confirm
the loser gets a clear "already decided" message, not a silent failure.

**API:** `GET /bookings`, `POST /bookings/{id}/approve`,
`POST /bookings/{id}/reject`.

### Phase 7 — End-to-end wiring, tests, AC sweep

- No new screens. Confirms every prior phase's screen is reachable through
  real navigation (resource → availability → book → my bookings → cancel;
  approver flow via the nav's Approvals item) rather than only demoed in
  isolation.
- Vitest coverage per service and per component with meaningful logic
  (form validation, the calendar's fetch/window logic, the cancel
  single-vs-series choice), mirroring WP-6's file-per-concern style.
- Manual walkthrough of all four WP-7 acceptance criteria against the real
  running backend.
- Write-up: this plan's outcomes recorded back into CLAUDE.md §12, same as
  every prior work package.

**Screens needed:** none.

**Demo:** the four ACs, shown live, end to end, with no mock data anywhere in
the path.

---

## 6. Folder conventions

Extending WP-6's layout, `features/` grows one subfolder per screen area:

```
frontend/src/app/
  core/                      unchanged from WP-6
  features/
    auth/                    WP-6
    placeholder/             WP-6 — shrinks as each nav item gets a real home
    resources/
      list/
      detail/
    availability/
    booking/
    my-bookings/
    calendar/
    approvals/
  layout/shell/              unchanged from WP-6
  shared/                    brand-mark (WP-6) + whatever WP-7 finds worth
                             extracting on its third use, same rule
```

Each feature gets its own thin API service (`resources.service.ts`,
`bookings.service.ts`, `recurrence-rules.service.ts`,
`availability.service.ts`) rather than one large API client — mirroring the
backend's per-feature-folder convention (decision `0015`) rather than
inventing a different shape on the frontend.

---

## 7. Notes

- **Flagged gap, not silently dropped**: resource admin CRUD (create, edit,
  archive, availability-window/approver/blackout management UI). The designs
  already assume it and the backend has supported all of it since WP-3, but
  it is not in WP-7's task list. Needs its own subsection when a future work
  package picks it up; until then the buttons stay absent from the UI rather
  than pointing at nothing.
- Each phase's own section above is the outline; the actual step-by-step
  breakdown (the granularity WP-3 through WP-6 used for review checkpoints)
  is added to this document **one phase at a time, immediately before that
  phase starts** — not drafted for all seven phases up front, per the owner's
  instruction for this package.
- Nothing here touches the backend. If a phase turns up a genuine contract
  gap (a field the UI needs that no response carries, an endpoint shape that
  doesn't fit the screen), that's a stop-and-ask per CLAUDE.md §11, not a
  silent backend patch mid-frontend-WP — same rule WP-6 closed with.
