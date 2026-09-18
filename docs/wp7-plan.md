# WP-7 — Booking UI & Calendar

Source: `docs/Work Packages - Week 5 and 6.pdf` (weeks 5–6, frontend track),
which carries WP-6 and WP-7 together. **That PDF is the authority on scope** —
the task list in CLAUDE.md §12 will be copied from it, and anything not in it
is a gap to flag rather than something to add on judgment (CLAUDE.md §11).

---

## Status

**In progress — three of six phases done**, and the fourth re-planned on
2026-09-18 after its first step had shipped (Phases 4 and 5 merged; see the
table below). Plan approved by the repo owner
2026-09-15, before any code was written, the same process WP-3 through WP-6
each went through. The owner has allocated more than a week to this package
and wants every phase built seriously rather than rushed; each phase is split
into its own smaller steps once it is about to start, not all up front.

| Phase | State | Tests at close |
|---|---|---|
| 1 — Resource list & detail | **Done** 2026-09-15 (5 steps) | 89 |
| 2 — Availability view | **Done** 2026-09-16 (7 steps) | 199 |
| 3 — Booking form (one-off + recurring) | **Done** 2026-09-17 (8 steps) | 462 |
| — Recurring-booking hardening pass | **Done** 2026-09-17 (7 findings) | 556 |
| 4 — Calendar, booking detail & cancellation (the hard problem) | **In progress** — re-planned 2026-09-18; steps 1–4 of 7 done | 695 |
| ~~5 — Calendar~~ | **Absorbed into Phase 4**, 2026-09-18 — number retired, not reused | — |
| 6 — Approval queue | Not started | — |
| 7 — End-to-end wiring + AC sweep | Not started | — |

**Phase 5's number is retired rather than reused**, and Phases 6 and 7 keep
theirs. Renumbering would silently falsify every existing reference to "Phase
5, the hard problem" — in [`docs/roadmap/wp7.md`](roadmap/wp7.md), in CLAUDE.md,
in this file's own earlier sections, and in the commit history — for no gain
beyond a tidier sequence. A gap in the numbering is cheaper than a document
that disagrees with the ones citing it.

Per-phase detail is in §5; the delivery narrative is in
[`docs/roadmap/wp7.md`](roadmap/wp7.md). Phase 1's own mid-phase pivot
(client-side filters reversed to real backend query params) is recorded in
CLAUDE.md's "Resource list filters extended for WP-7" entry. Phase 2 was
click-tested live by the owner; Phase 3 was taken as two branches at the
owner's own seam, the one-off half (steps 1–5) and the recurring half
(steps 6–8).

**Five calls settled with the owner along the way**, each written up where it
applies: idempotency is the recurring path only (§7's flagged gap covers the
one-off endpoint); manual one-off date/time entry is dropped in favour of
pre-fill-only; the phase did not wait for a design (one arrived mid-phase,
`design/booking_view_design.png`); the selected slot travels as **query
parameters** rather than router state (Phase 3 step 2); and a recurring
booking is reachable directly from a resource via **`?mode=recurring`**
rather than requiring a slot to be picked first (Phase 3 step 6), from both
the resource list card and the detail page.

**A hardening pass over the recurring half followed on 2026-09-17**, after
Phase 3 closed and before Phase 4 started — seven findings reviewed against
the code, six fixed, one (idempotency-key durability across a reload)
answered with a decision and on-screen copy rather than a migration. Full
narrative in [`docs/roadmap/wp7.md`](roadmap/wp7.md); the short version of
what changed is in CLAUDE.md's WP-7 subsection. Baseline moved from 462 to
**556 vitest tests**, and each new regression test was proven to fail against
the pre-fix code before being kept.

### Where things stand for the next session

- **Phase 4 was re-planned on 2026-09-18, after its step 1 had shipped** — the
  owner's call, and the reasoning is in §5's Phase 4 preamble. In short: a
  separate My Bookings *list* is redundant once a calendar exists, and the
  source PDF never asked for one. The calendar and the former Phase 5 are now
  one phase; `/my-bookings` is removed rather than built, and the calendar
  becomes the app's landing screen at `/calendar`.
- **Step 1 survived the re-plan untouched and unwasted.** `BookingsService.list()`
  is precisely the date-window-bounded fetch the calendar needs, and
  `getById()`/`cancel()`/`RecurrenceRulesService.cancel()` are what steps 4–6
  still call. Nothing built on 2026-09-18 was thrown away.
- **Phase 4's step list is rewritten and approved** (§5, seven steps). Of the
  three calls settled on 2026-09-17, one survives unchanged (decision `0002`'s
  TenantAdmin reach still defers to Phase 6), one is superseded (there is no
  list to open on Upcoming) and one is moot (the My Bookings design is no
  longer needed; a calendar design is, and §4 says so).
- **No My Bookings design will arrive, and none is wanted.** The owner
  confirmed on 2026-09-17 that one would land before the phase started; on
  2026-09-18 they cancelled the screen instead. The design this phase actually
  needs is the **calendar**, listed in §4 — and it is the one design worth
  waiting for, since the loading, empty and overflow states are exactly what
  the hard problem makes non-trivial.
- **Phase 4 inherits three things the hardening pass established** and should
  not re-litigate: server field messages take precedence over client ones and
  are cleared when their control is edited; anything that feeds a submit is
  disabled while it is in flight; and an outcome panel renders from what was
  submitted, not from live form state.
- **The browser walkthrough of Phase 3 is the one outstanding verification.**
  Every request/response pair the screens depend on is checked against the
  running API, and the vitest suite asserts rendering, but no automation
  exists in this environment to click the flow itself. Both bugs found during
  Phase 3 were found by the owner clicking, neither by the suite — so this
  gap is worth closing rather than discounting.
- **Open, deliberately**: `?mode` is read from the URL on arrival but the
  toggle does not write it back, so sharing a URL mid-form always shares the
  one-off view. A small change if it is wanted.
- **Open, owned elsewhere**: `POST /bookings` still has no idempotency key
  (§7), and the stale `SlotUnavailable`/`CapacityExceeded` comment inside the
  applied `AddCreateBookingProcedure` migration stays as-is per CLAUDE.md §5.
- **No new numbered decision docs were written for Phase 3's calls**, matching
  how Phases 1–2 recorded theirs: they live in this plan beside the step they
  govern. Promote any of them to `docs/decisions/` if they start being cited
  from outside WP-7.

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
about, rather than helping it. Revisit only if **Phase 4** finds the custom
component genuinely can't meet the responsiveness bar — flagged again in that
phase's own section, not assumed here. *(Said "Phase 5" until the 2026-09-18
merge; the escape hatch is unchanged, only which phase would take it.)*

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
| 1 | **Resource list** | Provided — `design/resources_design.png`. Admin actions (New resource, per-card `⋮` menu) not built; see §3. Each card gained a second action in Phase 3, "Recurring" (`?mode=recurring`) beside "Book resource" — not in the design, added because routing a series through the availability screen was a step nobody could skip. |
| 1 | **Resource detail** | Provided — `design/resource_details_design.png`. "Edit resource" not built; "Check availability" routes to Phase 2. Gained a second action in Phase 3, "Book a recurring series" (`?mode=recurring`) — not in the design, added because a series needs no picked slot. |
| 2 | **Availability view** | Needed before Phase 2 starts. |
| 3 | **Booking form** (one-off + recurring, validation states) | Provided mid-phase, 2026-09-17 — `design/booking_view_design.png`. Covers the one-time half only; the owner's instruction is that choosing "Recurring" expands the recurrence fields in place, under Title, in the same component. Deviations recorded in Phase 3's step 2 below. |
| ~~4~~ | ~~**My Bookings** (list, detail, cancel confirmation, series-vs-occurrence cancel choice)~~ | **Cancelled 2026-09-18** — the screen is not being built (§5, Phase 4's preamble). No design was ever provided and none is wanted. |
| 4 | **Calendar** (month/week view, event chip, empty/loading/overflow states) | **Provided 2026-09-18, before step 2 started** — `design/calendar_month_design.png` and `design/calendar_week_design.png`. Followed closely; the deviations (sidebar items, breadcrumb, a data-driven hour axis, and the three states the designs have no answer for) are recorded in step 2 below. |
| 4 | **Booking detail** (the cancellation trio, approval section, cancel confirmation, series-vs-occurrence choice) | Needed before Phase 4's step 4. What survives of the cancelled My Bookings design — it was always the half of that screen the FRs actually require (FR-4.4, FR-5.2, FR-5.3), and it is now reached from a calendar chip instead of a list row. |
| 6 | **Approval queue** (pending list, approve/reject with note) | Needed before Phase 6 starts. |
| 7 | *(none)* | Wiring and AC sweep only. |

---

## 5. Phasing

Six phases (seven until Phases 4 and 5 merged on 2026-09-18), each a working
increment against the real backend — no mocks,
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

### Phase 2 — Availability view — **Done** (2026-09-16)

Full narrative: [`docs/roadmap/wp7.md`](docs/roadmap/wp7.md). Design landed
mid-phase (`design/availability_view_design.png`) — followed closely, with
three deliberate deviations agreed with the owner up front: click-a-bar +
Start/End dropdowns instead of full click-and-drag-to-select (the drag
interaction was added back later, see step 6 below, once the simpler
version was working); a rolling 7-day window with prev/next arrows +
a plain two-date popover instead of a full custom calendar-grid date
picker; and no attempt to replicate the design's flanking-green-either-side
look for a narrowed selection — a separate burgundy overlay on top of the
full green bar instead, which reads more clearly than the mock's own
implied behavior.

**Steps:**

1. **`AvailabilityService` + models, no UI — done.** `availability.models.ts`
   (`LocalDateString`, `AvailabilityParams`, `BookableInterval`,
   `AvailabilityResponse`, mirroring `GetResourceAvailabilityQueryResponse`
   field-for-field) and `availability.service.ts` (one `get(resourceId,
   params)` call, `skipErrorToast()` since the screen renders its own inline
   states). **Tests:** `HttpTestingController` spec per param combination.

2. **Shared resource-type icon/label extraction — done.** The third
   occurrence (list, detail, now this screen) of the per-`ResourceType` SVG
   switch and label/capacity-label helpers, per `BrandMarkComponent`'s own
   "extract on third use" precedent flagged at the end of Phase 1. New
   `shared/resource-type/` (`ResourceTypeIconComponent`,
   `resourceTypeLabel()`/`resourceCapacityLabel()`); list and detail
   refactored to use it. Gotcha found: Angular's emulated style
   encapsulation means a parent component's CSS can't size a *child*
   component's own template elements directly (`.resource-icon svg` stopped
   matching once the `svg` moved into `ResourceTypeIconComponent`'s own
   template) — fixed by sizing the child's host element instead
   (`.resource-icon app-resource-type-icon`) and having the child fill
   whatever box it's given (`svg { width: 100%; height: 100% }`).

3. **Screen shell — done.** Route wiring (`:id/availability` swapped from
   Phase 1's placeholder), the resource summary card, loading/404/error
   states matching `ResourceDetailComponent`'s own pattern. Needed a
   `BreadcrumbService` addition: a second `insertBeforeLast` signal
   alongside the existing `override`, because this route is a *sibling* of
   `:id`, not a child of it — its own route-title chain never contributes a
   crumb for the resource itself, so there's nothing to replace (what
   `override` does for the detail page), only something to insert, to get
   `Resources > Conference Room A > Availability` (three crumbs) rather than
   clobbering "Availability" the way `override` alone would have.

4. **Date-range navigator + quantity stepper — done.** Rolling 7-day window
   (prev/next arrows, "Today"), a custom-range popover (two native date
   inputs) with the 90-day cap enforced client-side
   (`AvailabilityQueryRules.MaxRangeDays`, mirrored, not re-derived), a
   quantity stepper shown only for `Capacity > 1` (decision `0005`). New
   `local-date.ts`: pure calendar-date arithmetic (`resourceLocalToday`,
   `addDays`, `rangeLengthDays`, `formatLocalDate`) anchored to UTC
   internally so the *viewer's* own browser timezone can never silently
   shift a *resource's* calendar date — the exact class of bug decision
   `0003` exists to keep out. **Owner correction mid-step**: the popover
   could only be dismissed via its own Cancel button — added toggle-to-close
   (re-clicking the range display) and click-outside-to-close (a
   `document:click` listener checking `.closest('.range-picker-wrapper')`).

5. **Fetch + render the grid — done.** Wired to the range/quantity controls;
   stale-response guard mirroring `ResourceListComponent`'s own
   `latestRequestId`. New `availability-grid.ts` (kept out of the component
   since none of it is Angular-specific): `splitIntervalByLocalDay` (an
   interval can span local midnight — decision `0022`'s "23:59:59 means the
   following midnight" chaining into the next day's own window — so it's
   clipped into one `DaySegment` per local day), `buildDayRows`,
   `computeAxis`/`buildAxisTicks` (one shared 2-hour-tick time axis for the
   whole visible window), `minuteSpanStylePercent`. Archived-vs-closed
   distinction per decision `0020`. **A real bug found and fixed mid-step**:
   piping the availability fetch through `takeUntilDestroyed(this.destroyRef)`
   corrupted the Angular `TestBed` environment for every test running after
   one that destroyed the component mid-request — removed; harmless without
   it, since the method has no long-lived subscription to leak.

6. **Selection, the summary panel, and Continue to booking — done, then
   refined four more times at the owner's request.** Click a bar to select
   it; the "Selected time" panel (date/time/duration, an approval-required
   tag or remaining-units badge, Start/End dropdowns); "Continue to booking"
   navigates to a new `:id/book` placeholder route carrying `{ startUtc,
   endUtc, quantity }` via router state — **amended 2026-09-17 to query
   parameters** (`?startUtc=…&endUtc=…&quantity=…`, owner's decision; the
   reasoning is in Phase 3 step 2 below). It touched one line of this screen,
   `continueToBooking`. The core problem: turning a
   *narrowed* local-time selection back into a precise UTC instant, when
   only the original interval carries one — solved by giving each
   `DaySegment` its own `startUtc`/`endUtc`, walked forward from the
   interval's own start by local minutes elapsed rather than a full
   local-to-UTC converter (which would duplicate DST policy that's
   deliberately backend-only, §4.3); exact for every resource in this app's
   seed data, the one uncovered edge case documented in code rather than
   hidden, backstopped by `dbo.CreateBooking`'s own re-validation at
   submission time regardless. Then, in order: (a) min/max duration
   enforcement, deferred in the first pass, added into the Start/End
   dropdown bounds plus a defensive `durationError` check, and the default
   selection changed from "the whole segment" to "segment start through the
   resource's own max duration" so it's never invalid on click; (b) the
   selected bar no longer fills solid burgundy — it keeps its green fill
   with a thin outline, and a separate draggable `.selection-overlay` shows
   the exact sub-range on top; (c) the overlay's whole body became
   draggable too, translating both edges together by a 15-minute-snapped
   delta; (d) clicking anywhere outside the active selection now clears it,
   same as the explicit "Clear selection" link. All four used the Pointer
   Capture API (`setPointerCapture`) for dragging — mouse/touch/pen alike,
   no document-level listener teardown to get wrong.
   **Bug found by the owner on 2026-09-17 (while Phase 3 was in flight) and
   fixed the same day: the Start/End dropdowns displayed the wrong time.** On
   the seeded 3D Printer (min 60, max 180) clicking a bar selected 09:00–12:00
   — the overlay, the summary line and the eventual booking were all correct —
   but the End dropdown showed 10:00, its own *first* option (Start + the
   resource's minimum). Dragging the overlay or the Start handle moved that
   displayed number without moving the selection; only dragging the End handle
   made it agree, and from then on it behaved.
   Cause: `<select [value]="…">` with `@for`-rendered options. The binding
   sets the select's `value` *property* once, but a single select falls back
   to its first option whenever its option list is rebuilt — and
   `endTimeOptions` is rebuilt on every Start change, since it depends on
   `selectedStartMinutes`. Angular then doesn't re-apply the binding, because
   `selectedEndMinutes` itself never changed. Dragging the End handle
   "fixed" it only because that finally changed the bound value.
   Fixed by binding `[selected]` on each option instead, which survives the
   list being rebuilt because each option carries its own state, plus
   `withSelectedOption`, which adds the held value to the list when it falls
   between two steps (the option grid steps from the segment's own start,
   which a blackout can leave on an odd offset, while a drag snaps to
   absolute 15-minute marks — the two grids need not line up).
   **The lesson worth keeping**: every existing assertion about this
   interaction was at the signal level and every one of them passed while the
   screen was visibly wrong. The three regression tests added assert against
   the rendered DOM, and were confirmed to fail against the old template
   before the fix was kept. The resource list's own `[value]` select is *not*
   affected — its options are static and never rebuilt — so it was left alone.

   **Three further owner-requested changes, 2026-09-17** (alongside the
   dropdown bug above, and likewise during Phase 3):

   1. **Selecting a bar low in the grid no longer leaves it out of view.**
      Nothing was scrolling: the selection panel only exists once something is
      selected, and `.grid` is a flex child, so the panel taking its space
      shrinks `.grid-rows`' visible height from the bottom while `scrollTop`
      stays — which puts the row just clicked outside the shortened viewport.
      `selectSegment` now takes the click event and, in `afterNextRender` (the
      panel has to have taken its space before "is it still visible" can be
      answered), calls `scrollIntoView({ block: 'nearest' })` on the clicked
      button: the minimum scroll needed, and none at all when the bar is
      already fully visible, so a click near the top isn't yanked elsewhere.
   2. **The empty space between bars is accounted for** — by item 3's own
      labelled pills, not by a background track. A full-width silver bed
      (`.track-base`) was built first and **removed the same day** on the
      owner's correction: what was actually wanted was no *unexplained* gaps,
      which the "Unavailable"/"Booked" pills already deliver. Recorded rather
      than quietly reverted, since the first reading is a plausible one to
      arrive at again.
   3. **Unbookable time now says why** — "Unavailable" for a blackout,
      "Booked" for anything else inside opening hours. **No backend change
      was needed**, which was the owner's own condition: the availability
      endpoint answers with bookable time only (decision `0020`), but both
      missing pieces are already readable by a member —
      `ResourceDetail.availabilityWindows` for the opening hours, and
      `GET /resources/{id}/blackout-periods`, which sits on `TenantMember`
      precisely because (its own handler comment) "a member choosing when to
      book needs to see when a resource is blacked out". So the reason is
      *derived*: opening hours minus bookable time is unbookable time, and the
      blackout list splits that into the two labels. "Booked" is deliberately
      the fallback rather than a positive test — on a pooled resource, "some
      units left but fewer than you asked for" is also honestly "Booked".
      New `blackout-periods.service.ts`/`.models.ts`; the set arithmetic
      (`buildUnbookableSpans`, `subtractSpans`, `intersectSpans`,
      `openSpansFor`) lives in `availability-grid.ts` with the rest of the
      grid math, and `computeAxis` now considers unbookable spans too, so a
      day whose whole morning is blacked out still has an axis wide enough to
      draw it. A blackout is a UTC span like a bookable interval and goes
      through the same `splitIntervalByLocalDay`, so one that crosses local
      midnight lands on both days without a second conversion.
      **A closed day says nothing**: no window for that weekday means no
      label, because "closed" is not "unavailable" — the empty-day text
      already covers it. And the blackout fetch's failure is deliberately
      silent (no error state, no retry button): it can only ever turn "Booked"
      into "Unavailable", so without it the grid is still correct, just less
      specific.
      **One pill per continuous reason** (owner's correction, same day): the
      first version drew one 11:00–13:00 blackout as *two* "Unavailable"
      pills, 11–12 and 12–13. Not two blackouts — the dev database's 3D
      Printer carries two *touching* weekday windows (09:00–12:00 and
      12:00–17:00, replaced at some point by hand; `SeedData.AddWeekdayWindows`
      creates a single 09:00–17:00 window), and the gap was being cut at that
      boundary. Contiguous or overlapping windows now merge into one opening
      span before anything is subtracted, while windows with a real gap
      between them stay apart — merging across a genuine split shift would
      mislabel closed time as unbookable, which is the mistake in the other
      direction. The resulting spans are then merged again by kind, so two
      abutting blackout rows likewise read as one pill, while a blackout
      meeting booked time stays two.

   **A genuine debugging detour**, separate from the feature work: the
   owner's "only the grid scrolls, keep the chrome pinned" request took
   three attempts, because the first two relied on `height: 100%`
   percentage sizing through a chain that had an unbounded `display: block`
   link partway down (this component's own `:host`) — percentages don't
   resolve against an element with no definite height of its own. Fixed by
   making the chain flex-based end to end (`flex: 1; min-height: 0` at
   every link, starting from the shell's `.content`), not percentages.
   **Also raised `frontend/angular.json`'s `anyComponentStyle` error budget**
   (8kB → 16kB, warning left at 4kB) after this now-genuinely-complex screen
   hit the old ceiling twice in a row.

7. **Final verification — done, 2026-09-16.** `npx ng test --watch=false`:
   **17 test files, 199 passed, 0 failed.** `npx ng build`: clean (SCSS
   budget warning only). Unlike Phase 1, the owner performed the live
   click-through directly this session and confirmed the whole flow works —
   closing the verification gap Phase 1 had left open.

**Demo:** pick a range on Conference Room A (single-capacity, approval-gated)
and on Pool Cars (pooled) and see the different bar shape — a time range vs.
a remaining-count floor; select and narrow a bar on each, including a drag,
and confirm "Continue to booking" carries the right UTC span forward.

**API:** `GET /resources/{id}/availability`.

### Phase 3 — Booking form (one-off + recurring) — **Done** (2026-09-17)

**Plan written 2026-09-16**, before any code, same as every prior phase. The
step breakdown below is added now because this phase is next, per §7's rule
that a phase is broken down immediately before it starts and not sooner.

All eight steps are done. 462 vitest tests, 0 failed; `npx ng build` clean.
Taken as two branches at the owner's own seam — the one-off half (steps 1–5)
and the recurring half (steps 6–8).

- `BookingsService.create()` / `RecurrenceRulesService.create()`.
- One screen, a one-off/recurring toggle. The one-off half is pre-filled from
  Phase 2's selection; the recurring half is entered by hand (see "Three calls
  settled" below for why those two differ).
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

#### The contract this phase actually consumes

Read off the backend before planning, because the two endpoints are **not**
symmetric and the asymmetry drives most of the phasing below.

`POST /bookings` — `{ resourceId, startsAtUtc, endsAtUtc, quantity, title }`,
**UTC instants**, no idempotency support of any kind.
`CreateBookingCommandRequestValidator` additionally requires that each instant
carry a zone designator (a bare local-looking timestamp is `Unspecified` and is
refused) and carry **no fractional seconds** (`datetime2(0)` would round it,
and the response would then disagree with the row a client reads back —
CLAUDE.md §4.3). Both hold for anything Phase 2 hands over, since the
availability endpoint's own instants come out of `datetime2(0)` columns and
`addMinutesToUtc` only ever adds whole minutes — but the client truncates to
whole seconds on the way out anyway rather than relying on that chain staying
true. 201 returns `status` (`Confirmed` | `Pending`) and
`approval: { approvalRequestId, expiresAtUtc } | null`; it deliberately carries
no `remainingCapacity` (that record's own header explains why — a number a
client might act on invites the check-then-act race the procedure exists to
prevent).

`POST /recurrence-rules` — `{ resourceId, frequency, intervalValue,
localStartTime, localEndTime, startDate, endDate?, occurrenceCount?, quantity,
title }`, **resource-local wall clock**, never instants (decision `0003`: the
client names *when* the series runs, the handler resolves *what instant that
is*, per occurrence, via the resource's own `TimeZoneId`). There is
deliberately no `timeZoneId` field to send. Plus an `Idempotency-Key`
**header**, not a body field. 201 returns `{ recurrenceRuleId, occurrences[] }`
where each entry is `{ occurrenceDate, status, bookingId, reasonCode }` and
`status` is `Created` | `SkippedSpringForwardGap` | `Refused` (FR-5.4: never a
bare count).

**The all-refused case carries the identical breakdown on a 422.**
`NoOccurrencesCreatedException` puts it in `AppException.Extensions`, and
`GlobalExceptionHandler` copies those onto `ProblemDetails.Extensions`, so
`occurrences` arrives as a **top-level key on the problem response** — the same
level as `reasonCode` and `errors`, not nested. One renderer serves both the
201 and the 422; only the framing differs.

#### Three calls settled before the phase starts (2026-09-16)

**Idempotency is the recurring path only, and the one-off gap is flagged, not
papered over.** `POST /bookings` has no idempotency key — no header, no
operation record, nothing. Only `POST /recurrence-rules` does (the 2026-09-15
hardening pass, item 11, `RecurrenceCreationOperation`). Owner's call: this is
a frontend package and §7's rule stands — no backend change mid-WP. So the
recurring submit sends a key and the one-off submit does not, which means **a
one-off booking retried after a lost response can create a duplicate**. That is
a real gap with a real consequence, so it gets an owner rather than a comment:
recorded in §7 below against a future backend package, and mitigated as far as
the client can — the one-off submit never auto-retries, and a transport failure
tells the user their booking *may* have been created and links them to check,
rather than offering a retry button that could double-book them.

**Manual one-off date/time entry is dropped; the one-off path is pre-fill
only.** The earlier bullet here said the form was "also reachable directly from
a resource's detail page for a manual date/time entry" — reversed, because it
implies something the frontend should not be doing. `POST /bookings` takes UTC,
so a hand-typed local time would need an *unanchored* local→UTC inversion in
the client; `resourceLocalMinutesToUtc` only works anchored to a `DaySegment`
whose UTC bounds came from the server, and a general inverter would put
DST-ambiguity policy in the browser, which CLAUDE.md §4.3 keeps deliberately
backend-only. So: `/resources/:id/book` reached with no selection renders a
"pick a time first" state linking to the availability screen, and the detail
page's CTA keeps pointing at availability as it does today. **Recurrence is
unaffected and keeps full manual entry** — it sends local wall-clock times
natively and the backend does the conversion, which is exactly the split §4.3
wants.

**No booking-form design exists and the phase does not wait for one.** §4 lists
one as needed; `design/` has none. Built against `design/BookSpace Visual
Identity — Premium Burgundy.md` and the availability screen's own card /
summary-panel / form vocabulary, the same way Phase 2 ran before its design
landed mid-phase. If a design arrives later it is applied as a refinement step,
not a rebuild.

**Superseded 2026-09-17, the same way Phase 2's design did** — the booking
design landed mid-phase (`design/booking_view_design.png`), before step 2 was
written, so nothing had to be reworked. It is followed closely from step 2 on;
the owner's standing instruction is to adapt where it doesn't fit this app's
own flow or data and to follow it otherwise. Its one structural gap is
deliberate and already answered: it shows the one-time half only, and choosing
"Recurring" expands the recurrence fields **in place, under Title, in the same
component** rather than routing anywhere — which is what step 6 already
planned (one component, two form groups, not two routes).

**Steps:**

1. **Models + the two services, no UI — done, 2026-09-17.**
   `features/booking/booking.models.ts`
   (`BookingStatus`, `BookingApprovalDetail`, `CreateBookingRequest`,
   `CreateBookingResponse`) and `recurrence.models.ts` (`RecurrenceFrequency`,
   `RecurrenceOccurrenceReportStatus`, `RecurrenceOccurrenceReport`,
   `CreateRecurrenceSeriesRequest`, `CreateRecurrenceSeriesResponse`), mirroring
   the backend records field-for-field the way `resources.models.ts` and
   `availability.models.ts` already do. `bookings.service.ts` (`create()`) and
   `recurrence-rules.service.ts` (`create(request, idempotencyKey)`), both
   `skipErrorToast()` — this screen renders its own inline states, same
   reasoning as `ResourcesService` and `AvailabilityService`.
   **One shape to confirm against the live API rather than assume**, following
   `problem-details.ts`'s own precedent: what `TimeOnly`/`DateOnly` accept on
   the wire (the client will send `"HH:mm:ss"` and `"yyyy-MM-dd"` explicitly
   rather than rely on the shorter time form being parsed), and the exact
   casing of the `occurrences` extension and its members on a real 422.
   **Tests:** `HttpTestingController` specs for both bodies, the
   `Idempotency-Key` header being present on the recurring call and absent on
   the one-off one, and the mutually-exclusive end condition serializing as
   exactly one of the two fields.

   **Delivered, with the wire shapes confirmed live rather than assumed.**
   Both model files and both services landed as planned; 11 new vitest tests
   (254 total, 0 failed), `npx ng build` clean. Four things worth recording:

   - **The 422's `occurrences` shape was verified against the running API**,
     not inferred from the C# records — it had to be, because
     `NoOccurrencesCreatedException` puts the breakdown into
     `AppException.Extensions` as a boxed `object`, so its casing and its
     enum rendering depend on which JSON options serialize `ProblemDetails`
     rather than on the record's own declaration. A deliberately all-refused
     weekly series (03:00–04:00 local on a 09:00–17:00 resource) answered
     `422` with `occurrences` as a **top-level key beside `reasonCode`**,
     camelCase members, `status` as its name (`"Refused"`) and
     `occurrenceDate` as `"yyyy-MM-dd"` — byte-identical to the 201's own
     list, which is what lets step 7 use one renderer for both. Typed as
     `NoOccurrencesCreatedProblem extends ProblemDetails`. The probe left no
     trace: the all-refused path compensates its own up-front
     `RecurrenceRule` row away (`RemoveOrphanedRuleAsync`) before throwing.
   - **`TimeOnly`/`DateOnly` both accept what this client sends** —
     `"03:00:00"` and `"2026-09-20"`. The shorter `"03:00"` is *also*
     accepted, which is exactly why the client keeps sending the full
     three-part form: nothing here depends on the shorter form continuing to
     mean what it means today.
   - **The one-off validator's two instant rules were confirmed as real
     rejections**, since step 3 relies on them: `2026-09-21T13:00:00.500Z`
     comes back `400 ValidationFailed` with `errors.StartsAtUtc` (PascalCase
     key, as `problem-details.ts` already documents), and an interval in the
     past comes back `422 BookingInThePast`. Both are what step 3's truncation
     and step 5's field mapping are written against.
   - **`CK_RecurrenceRules_EndCondition` is mirrored in the type system**, not
     just in a runtime check: `RecurrenceEndCondition` is a union of
     `{ endDate; occurrenceCount?: never }` and
     `{ endDate?: never; occurrenceCount }`, so submitting both or neither is
     a compile error in the form rather than a 400 the form could have
     prevented. Its own test asserts against
     `JSON.parse(JSON.stringify(body))` rather than the request object, since
     what matters is that an explicitly-`undefined` key is *dropped* on the
     wire (a `null` one would not be).

   `RecurrenceRulesService.create(request, idempotencyKey)` takes the key as a
   **required** parameter even though the header is optional server-side: a
   caller that omits it silently gets the pre-hardening-pass behaviour (a
   fresh rule and a fresh attempt at every occurrence on any retry), which is
   the exact failure `RecurrenceCreationOperation` exists to prevent, so the
   decision is forced to the call site where the attempt's lifecycle is known.
   `BookingsService.create` carries the mirror-image comment — no retry
   anywhere above it, because that endpoint has no key at all (§7).

2. **The booking route's shell — resource load, arrival state, no form yet —
   done, 2026-09-17.**
   Replaces the `:id/book` placeholder in `app.routes.ts` with a real
   `features/booking/booking.component.ts`. Resource fetch through the same
   route-id-keyed `switchMap` pipeline `ResourceDetailComponent` and
   `AvailabilityComponent` both use since the 2026-09-16 frontend hardening
   pass (a stale fetch cancelled outright, not merely ignored), with the same
   loading / 404 / error-retry states. Breadcrumb via `BreadcrumbService`'s
   `insertBeforeLast`, not `override` — this route is a **sibling** of `:id`,
   exactly like `:id/availability`, so the resource's own crumb has to be
   inserted rather than replacing "Book".
   **Reads Phase 2's selected-slot contract** (`{ startUtc, endUtc, quantity }`).
   "No selection" is a normal state, not an error — it renders the "pick a time
   first" panel the manual-entry call above settled on. An archived resource
   renders the same not-bookable notice the list and detail screens already do.
   **Tests:** the arrival-with-selection and arrival-without-selection
   branches, the 404/error/retry paths, the breadcrumb insertion, and the
   archived case.

   **Delivered, against the design that landed the same day.** 37 new vitest
   tests (291 total, 0 failed), `npx ng build` clean — the booking chunk
   carries no new SCSS budget warning. The screen renders the design's own
   structure: the resource summary card (icon, name, type/capacity/approval
   chips, description, "View details →"), the panel area below it, and the
   "Need to make a change? … Back to availability" bar. Where the form goes,
   step 2 renders a one-line note; steps 3–7 fill it.

   **The selected slot travels as query parameters, not router state — the
   owner's decision, taken on review of step 2 and applied the same day.** The
   first implementation used router state (the History API's per-entry state
   object; ASP.NET's `TempData` is the closest analogue). It survives a reload,
   but it is invisible in the URL and cannot be shared, bookmarked or opened in
   a new tab, so "here's the slot, book it" was not expressible as a link and
   nothing — neither the owner nor a test — could see what the screen had been
   handed. `/resources/{id}/book?startUtc=2026-09-24T13:15:00Z&endUtc=…&quantity=1`
   is visible, shareable, survives a reload and works with back/forward.
   Alternatives weighed and rejected: a **shared signal service** (empty after
   a reload, and it makes the booking screen silently depend on the
   availability screen having run in this same session), **sessionStorage**
   (solves reload, not sharing, and adds staleness nothing owns), and a
   **backend "hold" record** (the strongest option, and what a ticketing site
   does — but it is a backend change mid-frontend-WP, §7, and it would put a
   second claim on capacity beside `dbo.CreateBooking`'s, with an expiry job
   and a cleanup path to match).

   **The URL is not a trust boundary, and nothing pretends otherwise.** Anyone
   can hand-edit these parameters — but `dbo.CreateBooking` re-checks
   availability, blackouts and peak capacity under its own lock at submit time
   regardless of what the client pre-filled, so an edited URL earns an ordinary
   `SlotUnavailable`/`OutsideAvailability` rejection, exactly as a stale shared
   link would. Nor does this reopen the "no manual date/time entry" call above:
   that was about the *client* never inverting local→UTC, and these parameters
   are UTC instants, same as router state was.

   **Both halves of the contract live in `booking-arrival.ts`** — the writer
   (`buildBookingQueryParams`, called by the availability screen) and the
   reader (`parseBookingSelection`) — so the parameter names exist once and
   cannot drift. That is why the *availability* feature imports from the
   *booking* feature here: the consumer owns the contract. Kept out of the
   components for the same reason `availability-grid.ts` was: none of it is
   Angular-specific (`QueryParamSource` is a one-method interface Angular's own
   `ParamMap` satisfies structurally) and the validation is worth testing on
   its own.

   **Validated, never cast**, and the rules mirror the server's so the form
   never builds a request the API would only refuse: both instants must carry a
   zone designator (`CreateBookingCommandRequestValidator.CarryAZone` — without
   this a zone-less `2026-09-24T13:15:00` in the URL would be read as the
   *viewer's* local time and silently shift the booking by their offset, the
   exact class of bug CLAUDE.md §4.3 keeps out of this client), the interval
   must be real and forward (`CK_Bookings_Interval`), and quantity must be a
   positive integer (`CK_Bookings_Quantity`, parsed with `Number` rather than
   `parseInt`, which would read `"2 rooms"` as 2). Anything else resolves to
   `null` — the ordinary "pick a time first" panel, not an error.
   What survives is normalized to whole-second UTC by a new
   `local-date.ts` helper (`toWholeSecondUtcIso`), used on both sides: the
   availability screen writes `…T08:00:00Z` rather than the `.000Z` its own
   `toISOString()` produces, and an offset form in a hand-edited URL resolves
   to the identical selection. `POST /bookings` refuses fractional seconds
   outright, so step 3's submit now has nothing left to truncate.

   **Deviations from the design, each deliberate:**
   - **No resource photo.** The design shows one in both the header card and
     the summary card; no resource carries an image on the API
     (`GetResourceQueryResponse` has no such field), and adding one would be a
     backend change inside a frontend package (§7's rule). The type icon every
     other resource screen already uses stands in its place.
   - **The page heading reads "Book resource" and so does the last crumb**,
     where the design has the heading "Book resource" over a shorter "Book"
     crumb. `ShellComponent` derives the heading *from* the last crumb on
     purpose (one source, so the two can't disagree), so honouring both would
     mean reopening that decision for one screen. The heading is the more
     prominent of the two, so it won.
   - **No "Home" crumb.** The design's breadcrumb starts at Home; this app's
     breadcrumb has been the matched route-title chain since WP-6 and no other
     screen shows one either. An app-wide breadcrumb change is not step 2's to
     make — flagged here rather than done quietly.
   - **An archived resource gets its own panel** ("Archived — not bookable"),
     which the design has no state for — the same treatment the list and
     detail screens already give in place of their booking CTAs, rather than a
     form that could only ever be refused with `ResourceArchived` on submit.
   - **Quantity is absent from the design** (its example is a single-capacity
     room). It arrives in step 3 as the same stepper Phase 2 uses, shown only
     for `Capacity > 1` (decision `0005`).

3. **One-off form + submit — done, 2026-09-17.** Title (optional, 200-char
   bound mirrored from
   `CreateBookingCommandRequestValidator.MaxTitleLength`), the quantity stepper
   (hidden entirely for `Capacity = 1`, per decision `0005`'s amendment —
   reusing Phase 2's own stepper behaviour), and a read-back of the selected
   span in the resource's timezone with the viewer's own zone named alongside
   it if they differ. Duration checked client-side against the resource's
   `minDurationMinutes`/`maxDurationMinutes` before submit — and specifically
   against `resource.minDurationMinutes` directly, **not** against the grid's
   15-minute UI step, which is the exact false-minimum bug the 2026-09-16
   frontend hardening pass fixed in `effectiveMinDuration`. Submit posts to
   `POST /bookings` with instants truncated to whole seconds.
   **Tests:** the request body built from arrival state, quantity bounds, the
   duration guard (including `null` min/max meaning "no rule", not "15
   minutes"), and double-submit being blocked.

   **Delivered.** 25 new vitest tests (316 total, 0 failed), `npx ng build`
   clean. The screen is the design's two-card layout: "Booking details" (the
   read-only Date / Time / Duration fields, the quantity stepper for a pooled
   resource, the Title input with its hint) beside "Booking summary" (the
   resource, the same four values as rows, the approval notice, and Confirm
   booking), with the "Need to make a change?" bar below — now hidden once a
   booking exists, since "go back and pick a different time" is the wrong
   advice at that point.

   **The one-time/recurring toggle is deliberately not here.** It belongs to
   step 6 together with the fields it reveals; rendering a dead "Recurring" tab
   now would ship a control that does nothing, and the owner is taking the
   one-off half as its own PR/branch — a half-wired toggle is exactly what
   should not be in it.

   **What the form guards, and what it deliberately doesn't.** Duration is
   checked against the resource's own `minDurationMinutes`/`maxDurationMinutes`
   and nothing else — `null` means "no rule configured", never a default,
   which is the false-minimum bug the 2026-09-16 hardening pass fixed one
   screen over and worth not reintroducing here. Title length mirrors
   `MaxTitleLength` (the input is also `maxlength`-bounded, so the check
   catches a paste that slips past it). A quantity above the resource's
   capacity is **refused** — it was briefly clamped instead, which silently
   booked something other than what was asked for; see step 5's own correction.
   **Not guarded, on purpose**: raising the quantity above what the
   availability query was answered for. Only `dbo.CreateBooking` can say
   whether a pool has room, so the form says so in a hint
   ("Availability was checked for 2 units…") and lets the request go — a
   `CapacityExceeded` rejection then reads as expected rather than arbitrary.

   **Double-submit is the one thing the client genuinely has to prevent**
   (§7's flagged gap: `POST /bookings` has no idempotency key, so a repeat
   creates a second booking). The button is disabled while a request is in
   flight and `confirmBooking` re-checks the same guard for anything reaching
   it programmatically; nothing retries, anywhere.

   **Two readings of the same span, when they differ.** The Date/Time fields
   are the resource's own timezone (decision `0003` — the zone the
   availability question was asked in), with a line naming the viewer's own
   zone and the same span in it when the two differ, so a member in Sarajevo
   booking a New York room knows when to actually be there. An overnight span
   carries the end's own date, since it lands on two calendar days.

   `formatDurationWords` ("2 hours 15 minutes") moved from
   `availability.component.ts` into `local-date.ts` at its **second** caller
   rather than the usual third: it is user-visible copy rendering the *same*
   duration on two screens in one flow, so a second copy that drifted would be
   a visible inconsistency, not merely duplicated code.

   **Verified against the running backend**, not only by unit tests: the exact
   body this form builds was posted to the real API for both resource kinds —
   Conference Room A answered `201 Confirmed` with `approval: null`, the
   approval-gated 3D Printer answered `201 Pending` with a real
   `approvalRequestId` and a 24-hour `expiresAtUtc` (FR-7.4), and re-posting
   the same slot answered `409 SlotUnavailable`. Both test bookings were
   cancelled afterwards, so the dev database carries only the two cancelled
   rows. Those three responses are exactly what steps 4 and 5 render.

   **Left for the steps that own them**: the created-booking panel is a
   one-line placeholder (step 4 makes it the real Confirmed-vs-Pending
   confirmation), and a submit failure shows one generic sentence (step 5
   replaces it with the reason-code catalogue, message *and* placement).

4. **Success, and `Pending` shown as its own outcome — done, 2026-09-17.**
   FR-7.1 — a booking on
   an approval-gated resource comes back `Pending`, and the member has to be
   told at the moment of booking, not left to discover it in a list later. A
   confirmation panel rather than a toast: `Confirmed` states the reserved span
   plainly; `Pending` names who decides (from `ResourceDetail.approvers`, which
   this screen has already loaded) and when the request expires (from
   `approval.expiresAtUtc`), and says explicitly that the slot is **not** held
   as confirmed.
   **A sequencing detail, not an oversight**: "View my bookings" points at the
   `my-bookings` route, which is still WP-6's placeholder — Phase 4 gives it a
   real destination. The link is correct today and simply becomes useful then;
   it is not a dead route.
   **Amended 2026-09-18**: Phase 4 gives it a real destination by *removing* it.
   `/my-bookings` is deleted and this link repoints to `/calendar` (Phase 4's
   landing-screen section). The sequencing point stands — the link was never
   dead — only the destination changed.
   **Tests:** both outcomes rendering distinctly, the approver list and expiry
   appearing only on `Pending`.

   **Delivered.** 14 new vitest tests (349 total, 0 failed), `npx ng build`
   clean. The panel replaces the form entirely once a booking exists, and the
   "Need to make a change?" bar goes with it — going back to pick a different
   time is advice for a booking that hasn't happened yet.

   **Everything in it reads the create *response*, never the selection the
   form was built from.** The two agree today; a panel that quietly showed the
   request instead of the response would be the wrong one to trust if they
   ever didn't. That is asserted directly — a response naming a different span
   than the form asked for renders the response's.

   `Confirmed` states the reservation plainly. `Pending` says, in the lead
   sentence rather than a footnote, that the time is **not held yet**; names
   who decides from `ResourceDetail.approvers` (already loaded — nothing extra
   is fetched), falling back to "a tenant administrator" when a resource lists
   none, since decision `0018` leaves a TenantAdmin able to approve anything
   in the tenant; and gives the expiry from `approval.expiresAtUtc`, with
   FR-7.4's "no configured expiry" getting its own sentence rather than a
   blank date. Colour (green vs amber) carries the same distinction the
   heading and copy do rather than being the only thing that does.

   **One deliberate mixing of zones**, worth knowing: the booked span reads in
   the *resource's* timezone (plus the viewer's own, when they differ), the
   same as the form above it — but the approval expiry reads in the
   **viewer's** zone, named explicitly. An expiry is not a fact about the
   room's schedule; it is a deadline a person watches.

5. **One-off rejection rendering — every reason code, none generic — done,
   2026-09-17.** One map
   from reason code to message *and placement*, in its own file so the
   catalogue is greppable against `ReasonCodes` rather than scattered across
   throw-site-shaped `if`s:
   - Top-of-form, with a "re-check availability" action back to Phase 2's
     screen, because the slot genuinely moved: `SlotUnavailable`,
     `CapacityExceeded` (409s — and per CLAUDE.md §6 these split by what is
     *left*, so `CapacityExceeded` is worded as "fewer units free than you
     asked for" and can only ever appear on a pooled resource).
   - Top-of-form, no retry action, because re-checking cannot help:
     `ResourceArchived`, `BlackoutPeriod`, `OutsideAvailability`,
     `BookingInThePast` (422s).
   - Field-level: `BookingDurationOutOfRange` against the duration control,
     and `ValidationFailed`'s `errors` dictionary mapped by **PascalCase**
     backend property name → form control, the translation `LoginComponent`
     already established in one place (`BACKEND_FIELD_NAMES`) rather than at
     every reader.
   - The two non-catalogue cases the stack can still produce:
     `ConcurrencyConflict` (409 from `DbUpdateConcurrencyException`) and
     `status === 0` (never reached a server — `HttpErrorResponse.error` is a
     `ProgressEvent`, not a `ProblemDetails`, so it must be checked *before*
     any reason-code branch, exactly as `handleLoginError` does). The latter is
     where the flagged one-off idempotency gap surfaces in the UI: "your
     booking may have been created" plus a link to check, never a retry button.
   - `ResourceNotFound` (404) reuses step 2's own not-found state.
   **Tests:** one per code, asserting message *and* placement, plus the
   status-0 branch taking priority over reason-code handling.

   **Delivered.** `booking-rejection.ts` (`describeBookingRejection`) resolves
   an error into message, placement and which actions to offer; the component
   never branches on a reason code itself. 27 new vitest tests (376 total, 0
   failed), `npx ng build` clean.

   **Every mapped code was triggered against the running API**, not assumed —
   a typo in a code string fails silently into the generic message, which is
   exactly the failure this catalogue exists to prevent. Confirmed live:
   `OutsideAvailability` (422, a 03:00 local slot), `BlackoutPeriod` (422,
   booking into the Sep 23 blackout), `BookingDurationOutOfRange` (422, both
   under the 60-minute minimum and over the 180-minute maximum),
   `ResourceNotFound` (404), `CapacityExceeded` (409), `ValidationFailed`
   (400, an over-long title) — plus `SlotUnavailable` (409) and
   `BookingInThePast` (422) from step 3's own probes. Nothing was created:
   every one is a refusal.

   **A discrepancy this turned up, flagged not fixed** (CLAUDE.md §6): that
   section says an exclusive resource "can only ever produce
   [`SlotUnavailable`], since `Capacity = 1` admits no quantity but 1". The
   live API answers **`CapacityExceeded`** for `quantity: 3` on a capacity-1
   resource — because the validator deliberately has no upper bound on
   quantity (`CreateBookingCommandRequestValidator`: "what is too many depends
   on the resource's Capacity, which this cannot see"), so an over-large
   quantity *can* be asked for and is refused by the procedure. The rule's
   premise holds for what can succeed, not for what can be sent. Owner's call
   whether to reword §6.
   Handled on the client, and **the first attempt at handling it was wrong**:
   the form clamped a URL-supplied quantity down to the resource's capacity,
   so the owner's own `?quantity=16` on the single-unit 3D Printer booked
   *one* unit and reported success, having silently changed what was asked
   for. Corrected the same day: the requested quantity is kept, and
   `capacityError` refuses it before any request goes out — the same instinct
   decision `0015` applies server-side when it rejects an oversized `pageSize`
   rather than clamping it, and the same pre-flight the duration check already
   gets. Placement follows where the member can actually act: against the
   stepper when there is one (stepping back within capacity clears it), and at
   the top of the form with a link back to availability when there isn't —
   an exclusive resource renders no stepper at all (decision `0005`), so a
   field-level message would have nothing to attach to.

   **Placement, as built:** `SlotUnavailable`, `CapacityExceeded` and
   `ConcurrencyConflict` are top-of-form **with** a link back to availability;
   `ResourceArchived`, `BlackoutPeriod`, `OutsideAvailability` and
   `BookingInThePast` are top-of-form **without** one, because re-checking
   cannot change a rule refusal and offering the action would imply it might.
   `BookingDurationOutOfRange` lands on the duration control;
   `ValidationFailed` maps `Title`/`Quantity` onto their own controls and
   anything else (the instants, the resource id — fields this form has no
   control for, since they come from the URL) to a top-of-form "pick a slot
   again". `ResourceNotFound` hands over to step 2's not-found state.

   **The two unknown-outcome cases share one answer.** A `status === 0` (the
   request reached no server; `HttpErrorResponse.error` is a `ProgressEvent`,
   so this is checked *before* any reason-code branch) and a 5xx (which did
   reach the server, so `dbo.CreateBooking` may well have committed) both say
   the booking **may** have been created and link to My Bookings. Neither
   offers a retry, anywhere — §7's idempotency gap as the member experiences
   it.
   **Amended 2026-09-18**: this link and its copy are the one piece of the
   merge that is a rewrite rather than a repoint. On a list, "check My Bookings"
   was sufficient because a booking that had in fact been created would be at
   the top; on a calendar the member has to be told *where to look*, so the
   message names the date and deep links `/calendar` to it. Phase 4's step 2
   owns the change; it is called out here so it is not swept up in a
   find-and-replace.

6. **The recurring toggle and its fields — done, 2026-09-17.** A segmented
   one-off/recurring
   control on the same screen (one component, two form groups — not two
   routes), with the recurring group carrying frequency (`Daily`/`Weekly`/
   `Monthly`), interval value, local start and end time, start date, and an
   end condition as a radio between end date and occurrence count — mutually
   exclusive, mirroring `CK_RecurrenceRules_EndCondition`, so the form can
   never submit both or neither.
   Pre-filled from the same arrival state via `local-date.ts`'s existing
   `utcToResourceLocal` (Phase 2 already built exactly this UTC→resource-local
   reading for the grid), so switching from one-off to recurring keeps the time
   the user already picked instead of blanking the form.
   **Client-side guards that mirror the server's rather than invent their
   own**, the same way Phase 2 mirrored `AvailabilityQueryRules.MaxRangeDays`:
   end time after start time, and decision `0007`'s two-year span cap for both
   end-condition paths — using the same arithmetic
   `CreateRecurrenceSeriesCommandRequestValidator.IsWithinMaxSpan` /
   `IsOccurrenceCountWithinMaxSpan` use, so the two cannot disagree about what
   "within two years" means and the user is not told by a 400 what the form
   could have told them immediately.
   **Copy that prevents a real misreading**: `Weekly` takes no weekday field —
   the weekday comes from `StartDate` — so the control says so rather than
   leaving the user hunting for a day picker that does not exist.
   **Tests:** the end-condition exclusivity, the span guard at and past the
   boundary for each frequency, the one-off→recurring pre-fill, and the
   local-time fields serializing as `"HH:mm:ss"`.

   **Delivered.** The toggle sits at the top of "Booking details" (the
   design's own control) and the recurring group appears **under the Title**,
   the owner's own placement. The form's logic lives in `recurrence-form.ts`
   — value shape, guards, the span arithmetic and the request builder — kept
   out of the component for the same reason `availability-grid.ts` is. 42 new
   vitest tests (418 total, 0 failed), `npx ng build` clean.

   **The span guard was checked against the live validator at its own
   boundaries**, which is the only way to know the two agree rather than merely
   look similar. Posting series that are all-refused for an unrelated reason
   (03:00 local, outside every window) means the response says what the
   *validator* decided while creating nothing: 105 weekly occurrences → 422
   `NoOccurrencesCreated` (span accepted), 106 → 400 `ValidationFailed` on
   `OccurrenceCount`; `endDate` 2028-09-24 → 422 (the cap itself is inside),
   2028-09-25 → 400 on `EndDate`. The client draws both lines in exactly the
   same places.

   **Two arithmetic details that had to match .NET rather than JavaScript.**
   `DateOnly.AddMonths` clamps onto a shorter month (Jan 31 + 1 month = Feb 28)
   where JS's `Date` rolls over into March, and `AddYears` clamps a leap day
   the same way — so `local-date.ts` gained `addMonths`/`addYears` that clamp.
   Without them the form and `CK_RecurrenceRules_MaxSpan` would disagree about
   which monthly series fit inside two years.

   **What is deliberately not here:** the submit. Step 7 owns
   `POST /recurrence-rules`, its `Idempotency-Key` lifecycle and the
   per-occurrence report, so in recurring mode the Confirm button stays
   disabled with a line saying so — submitting through the one-off path would
   create a single booking for a member who asked for a series, and that is
   asserted rather than assumed. The form itself is fully live meanwhile:
   every guard runs as you type.

   **Also decided here:** the one-off half's read-only Date/Time/Duration trio
   is **hidden** in recurring mode rather than left on screen. The series names
   its own start date and times in the group below, and showing the single
   slot's as well would be the same fact twice, free to disagree the moment
   either is edited.

   #### The slot is no longer a toll booth (owner's call, 2026-09-17)

   Step 6 first shipped with the recurring times **editable** and a picked slot
   still **required** to reach the form at all. The owner challenged both, in
   two rounds, and both challenges were right in different ways.

   **Round 1 — should the times be read-only?** The argument for: changing them
   invalidates whatever the availability screen checked, so server refusals
   become an ordinary outcome. The argument that won: a recurring series was
   never checked in the first place. The availability query answered one
   question about one slot; a 12-week series books eleven more dates no query
   was ever asked about, which is exactly why `POST /recurrence-rules` is
   best-effort per occurrence and why FR-5.4 makes the per-occurrence report
   the primary result rather than an error path. Locking the fields would
   verify occurrence 1 of N and cost the ability to express "every Monday" or
   "monthly on the 1st" without hunting for a matching slot. It also would not
   have prevented the failure actually worth preventing — an *all-refused*
   series from a time outside the resource's hours, which survives locking
   (move the start date to a Sunday on a Mon–Fri resource and everything is
   refused with the times still locked).
   **What was built instead: the window guard.** `outsideOpeningHours` checks
   the entered time against `ResourceDetail.availabilityWindows`, already
   loaded, so nothing is fetched: for `Weekly`, against the start date's own
   weekday, since every occurrence shares it; for `Daily`/`Monthly`, against
   every weekday, since occurrences land on varying ones and partial refusals
   there are legitimate information the report explains. Only a time that fits
   *no* weekday is refused outright. "Partly open is not open" —
   containment, not overlap, matching `OutsideAvailability`'s own rule.

   **Round 2 — then what is the availability screen for, if a recurring booker
   edits everything anyway?** This one landed: requiring a slot made the
   screen a toll booth, and the ritual of picking a slot nobody cares about was
   step 2's gate, not the recurring form's fault. So:
   - **`?mode=recurring` is part of the URL contract** (`booking-arrival.ts`,
     beside the selection params), and the resource detail page gained a
     **"Book a recurring series"** link that goes straight there — a member who
     knows their pattern never touches the calendar.
   - **"Pick a time first" now applies to the one-off half only**, inline where
     its fields would be rather than replacing the screen, so the toggle out of
     that state stays reachable. The one-off half still requires a slot: it is
     pre-fill only, and this client owns no local→UTC inversion to invent
     instants with (CLAUDE.md §4.3).
   - **Defaults come from the resource's own schedule** when no slot was
     picked (`defaultRecurrenceFor`): the first upcoming day it is actually
     open, at that day's opening time, for the shortest length it allows — so
     the form opens valid rather than blank, which its own test asserts.

   The availability screen keeps a real role for recurring bookings, just not a
   compulsory one: picking a slot first fixes a weekday and a time the resource
   is provably open at, so the whole series inherits a schedule likely to
   succeed, and occurrence 1 is verified for free.

7. **Recurring submit and the per-occurrence report — done, 2026-09-17.** The
   `Idempotency-Key` lifecycle, documented in the component rather than left
   implicit because getting it backwards fails in both directions: **one GUID
   per submission *attempt***, reused only when retrying that same attempt
   after a transport failure (network error, 5xx), regenerated whenever the
   user changes the form and submits again. Reuse it too eagerly and a
   deliberate second series silently resolves to the first; regenerate it on a
   retry and a crash-resumed request creates a duplicate series — which is the
   precise thing `RecurrenceCreationOperation` was built to prevent.
   One occurrence-report component renders both the 201 and the 422, since the
   payload is byte-identical and only the framing differs: a created/skipped/
   refused summary line, then the per-date breakdown — `Created` with its
   booking, `SkippedSpringForwardGap` worded from decision `0008` ("this date's
   start time does not exist — the clocks moved forward; skipped rather than
   shifted"), and `Refused` carrying its own reason code through step 5's same
   catalogue. FR-5.4's whole point is that no conflicting occurrence is dropped
   silently, so the breakdown is the primary result of a recurring submit, not
   a detail behind a disclosure.
   **Tests:** all three occurrence statuses rendering, the 422 and the 201
   producing the same breakdown, and the idempotency-key lifecycle (stable
   across a forced retry, fresh after a form edit).

   **Delivered.** `recurrence-outcome.ts` turns either answer into one
   `SeriesOutcome`, and one panel renders both — the summary line, then the
   per-date breakdown, which FR-5.4 makes the *primary* result rather than a
   detail behind a disclosure. 44 new vitest tests (462 total, 0 failed),
   `npx ng build` clean.

   **"The same attempt" is decided by the request body, not by a dirty flag.**
   The key is minted on submit and stored beside a `JSON.stringify` of exactly
   what was sent; a later submit whose body is byte-identical is that same
   attempt and reuses the key, and any edit produces a different body and so a
   fresh one — with nothing having to remember to invalidate anything. The key
   survives only an *unobservable* outcome (status 0, 5xx); every definitive
   answer clears it, because a refusal created nothing and the next submit is a
   new attempt rather than a resumption of that one.

   **This is the one write in the app that can honestly offer a retry**, and the
   UI says why: "Trying again is safe — this request carries a key that
   resolves to the same series." The one-off half deliberately offers no such
   button (§7's gap), and a test asserts the retry affordance appears for the
   recurring half only.

   **Verified end to end against the running API**, not only by mocks: a real
   weekly series (3 occurrences, Wednesdays 10:00–11:00) came back `201` with a
   genuinely mixed report — 1 `Refused` (`SlotUnavailable`) and 2 `Created` —
   and **re-posting the identical body with the same `Idempotency-Key` returned
   the same `recurrenceRuleId` and the same two booking ids**, which is the
   property the retry button rests on, proven rather than assumed. The series
   was cancelled afterwards (`POST /recurrence-rules/{id}/cancel`, both
   occurrences freed), so the dev database carries only cancelled rows.

   **A gap the tests caught, not review**: the all-refused panel told the
   member to "adjust the series and try again" while offering no way back to
   the form — the outcome panel had replaced it. It now has an **"Adjust the
   series"** action that returns to the form with every field as it was; the
   test that failed is the one that asserts a fresh key on the next attempt,
   which could not be written without it.

8. **Final verification — done, 2026-09-17.** Full `npx ng test --watch=false`
   and `npx ng build`, plus a live click-through against the real running
   backend — which Phase 2 established as the bar, and which Phase 1 had to
   leave open. Specifically: a one-off on an approval-gated resource landing
   `Pending`; a one-off into a deliberately-taken slot producing the right 409;
   a recurring weekly series with at least one forced rejection so the
   breakdown is genuinely exercised; and an all-refused series proving the 422
   path renders the same way the 201 does.

   **`npx ng test --watch=false`: 26 files, 462 passed, 0 failed.**
   `npx ng build`: clean, three SCSS budget *warnings* (booking 9.75 kB,
   availability 9.32 kB, resource list 4.69 kB against a 4 kB warning
   threshold and the 16 kB error ceiling the 2026-09-15 pass set).

   **All four scenarios were exercised against the running API**, with the
   exact request shapes the client builds — so what was checked is the contract
   the screen actually depends on:
   - **Pending**: a one-off on the 3D Printer → `201`, `status: "Pending"`,
     `approval.approvalRequestId` set and `expiresAtUtc` 24 hours out (FR-7.1,
     FR-7.4) — the two fields the outcome panel branches on.
   - **409**: the identical slot again → `409 SlotUnavailable`, the code the
     top-of-form message and its "check availability" action key off.
   - **A mixed series**: a blocking one-off placed on the second occurrence's
     date, then a weekly series over it → `201` with
     `Created / Refused(SlotUnavailable) / Created` — a genuinely mixed
     breakdown, not a happy path (FR-5.4).
   - **All refused**: the same series at 03:00 local → `422
     NoOccurrencesCreated` carrying three `Refused(OutsideAvailability)`
     entries, **structurally identical to the 201's own `occurrences`**, which
     is the property that lets one panel render both.

   Everything created was cancelled afterwards (two bookings and the series,
   whose cancel freed both live occurrences), so the dev database carries only
   cancelled rows from this pass. The five live bookings left in it are the
   owner's own, from clicking through.

   **Still outstanding, and flagged rather than claimed**: the browser
   walkthrough itself. No automation is available in this environment, so what
   is verified is every request/response pair the screens depend on, plus
   rendering assertions in the vitest suite — not the rendered flow end to end.
   The owner's own click-throughs during the phase covered much of it (the
   quantity bug and the dropdown bug were both found that way, neither by the
   suite), but a single pass over browse → availability → book → outcome, in
   both modes, is the remaining check before Phase 3 is signed off.

   **Also in this step**: removed `.panel--empty`, left behind when the mode
   toggle replaced the full-page "pick a time first" panel with an inline
   notice.

**Screens needed:** booking form — none provided; built against the visual
identity, per the third call above.

**Demo:** book a one-off slot on an approval-gated resource (see it land
Pending); book a recurring weekly series across a few weeks and see the
per-occurrence report, including at least one deliberately-forced rejection
(e.g. into an already-fully-booked slot) so the breakdown is genuinely
exercised, not just the happy path.

**API:** `POST /bookings`, `POST /recurrence-rules`.

### Phase 4 — Calendar, booking detail & cancellation (the hard problem)

**Re-planned 2026-09-18, the owner's call, after step 1 had already shipped.**
This phase was "My Bookings" and the next one was "Calendar"; they are now one.
The original Phase 4 section is not preserved verbatim — the step list below
replaces it — but everything that changed and why is recorded here rather than
quietly rewritten, because the reasoning is the part worth keeping.

#### Why the two phases merged

**A separate My Bookings list is redundant once a calendar exists**, and the
source PDF never asked for one. Its task list names a *calendar view rendering
bookings* and *cancellation and blackout handling in the UI*; "My Bookings" was
this plan's own decomposition — somewhere to hang FR-4.4 and FR-5.3 — not a
mentor requirement. So this is not scope cut against the work package. It is a
better decomposition of the same scope, and CLAUDE.md §12's rule that the
roadmap mirrors the WP rather than an independently-invented build order is
satisfied more closely afterwards than before.

**Skipping the phase outright would have dropped four things the calendar does
not give for free**, each required by an FR or named in the task list, so they
survive the merge as steps 3–5 below:

1. The **booking detail** read — the calendar needs a click target, and it is
   the only place the cancellation trio and the approval section are readable.
   FR-5.2 also asks that each occurrence be "independently viewable", which
   reads as a real route.
2. **Cancelling a booking** — "cancellation handling in the UI" is a literal
   task-list item, and FR-4.4's second half.
3. **The occurrence-vs-series choice** — FR-5.3 is explicit that both are
   possible, and it must never be one ambiguous button.
4. **Blackout-driven cancellations rendering their reason** — decision `0019`'s
   text snapshot and `Booking.CancelForBlackout`'s deliberately null actor.
   Also literally in the task list ("blackout handling").

What genuinely went away is the paged row list and its URL filters — the old
steps 2 and 3, and nothing else.

**Step 1 survived untouched and was not wasted work.** `BookingsService.list()`
is exactly the date-window-bounded fetch this phase's whole strategy rests on,
and `getById()` / `cancel()` / `RecurrenceRulesService.cancel()` are what steps
3–5 call. That it was built against a screen that no longer exists cost
nothing, because it was always a contract rather than a screen — which is why
it was taken as its own step.

#### Which statuses the calendar draws (settled 2026-09-18)

A contract detail drives this, and it is worth stating before the steps because
it shapes the fetch: **`status` is a single value server-side**
(`ListBookingsQueryRequest.Status` is a `BookingStatus?`, not a set), so the
calendar *cannot* ask for "everything except cancelled" in one request. It
fetches the visible window unfiltered and drops what it will not draw
client-side. That is affordable precisely because the window is already
bounded — it is the same fetch either way — but it makes this a rendering rule
rather than a query parameter, and so a rule that has to be written down for
all six statuses instead of just the one the owner asked about.

| Status | Drawn? | Why |
|---|---|---|
| `Confirmed` | Yes | The ordinary case. |
| `Pending` | Yes, distinctly | It *is* holding the slot provisionally (FR-7.1), and "is my request approved yet?" is the question a calendar otherwise answers badly. The booking screen already distinguishes the two outcomes at creation; the calendar keeps that distinction rather than flattening it. |
| `Completed` | Yes, muted | Honest history: the slot really was occupied. |
| `NoShow` | Yes, muted | Same — a past slot that was held and not used. Nothing writes this today (the no-show job is out of WP-7's scope), so it is a rendering rule waiting for data, not a state to go looking for. |
| `Cancelled` | **No** | It holds no time, so it has no cell to occupy. The member is told by email — `NotificationKind.Cancelled` and `SeriesCancelled` both exist — so this is not a silent disappearance. |
| `Rejected` | **No** | The same shape as `Cancelled`: an approver said no, `NotificationKind.Rejected` fires, and nothing is being held. Owner's call 2026-09-18, taken deliberately rather than inherited from the `Cancelled` decision. |

**What this costs, stated rather than glossed:** a member cannot read *why* a
booking was cancelled after the fact — including decision `0019`'s blackout
reason snapshot, which is the one case where the explanation is genuinely
interesting. The email carries the fact; the in-app record becomes unreachable
once the chip is gone. Accepted by the owner on 2026-09-18 as a fair trade
against building a second screen to house it. Worth revisiting if members start
asking why a booking vanished — the fix at that point is a small "recently
cancelled" affordance on the calendar, not a resurrected My Bookings.

#### The calendar becomes the landing screen (settled 2026-09-18)

`/my-bookings` is **removed**, not repointed, and the calendar takes the home
slot. Home has been a placeholder since WP-6 and **nothing in the PRD, CLAUDE.md
or any work package ever assigned it a job** — checked, not assumed — so this
displaces nothing. A dashboard nobody has designed is worth less than the screen
that answers the question members actually open this app with, and landing on
your own schedule after signing in is simply right.

Mechanically, and each of these is a real edit rather than a rename:

- **The route is `/calendar`, with `/home` redirecting to it** and `''`
  pointing there. The URL then says what the page is — the same instinct that
  moved Phase 3's selected slot into query parameters — and the calendar wants
  `?view=`/`?date=` in the URL anyway, exactly as Phase 2's date navigator
  does. It also leaves `/home` free if a real dashboard is ever wanted.
- **The nav item becomes "Calendar"**, and "My Bookings" is deleted rather than
  relabelled. The `bookings` icon it used is freed.
- **The route title becomes "Calendar".** `ShellComponent` derives the page
  heading *from the last crumb* deliberately (one source, so the two cannot
  disagree), so a route still titled "Home" would put a calendar under the
  heading "Home".
- **Three links in `booking.component.html` need repointing, and one needs
  rewriting.** Two say "View my bookings" and simply change. The third
  (currently line 725) is the entire client-side mitigation for §7's one-off
  idempotency gap — *"your booking may have been created, go check"* — and on a
  list that was enough, because a new booking would be at the top. On a calendar
  the member has to know **where to look**, so that copy names the date and deep
  links the calendar to it. Treating it as a find-and-replace would quietly make
  it worse, which is why it is called out here rather than left to the step.
- **The heaviest screen in the app now loads on every login.** Not an objection
  — it is what the member wants — but the loading and empty states stop being
  an afterthought and become first-impression surface, which is why §4 flags
  them as the part of the calendar design actually worth having.

#### The contract this phase actually consumes

Read off the controller and the DTOs on 2026-09-17, not off this document's
own §2 summary — the same check Phase 3 ran before it started, and worth
repeating because three of these constraints are easy to get wrong from
memory.

| Endpoint | What it returns, and the parts that constrain the UI |
|---|---|
| `GET /bookings` | `PagedResult<ListBookingsQueryResponse>`. Filters: `from`, `to`, `status`, `resourceId`, `page`, `pageSize`, `sort`. The row carries `resourceName` and `userName` denormalized on, plus `recurrenceRuleId`, the span, `quantity`, `title`, `status` — **and none of the cancellation fields.** |
| `GET /bookings/{id}` | `GetBookingQueryResponse`: everything on the row plus `checkedInAtUtc`, the cancellation trio, `createdAtUtc`/`updatedAtUtc`, and `approval`. **404 for anything this caller may not see, never 403** — another member's, another tenant's and a nonexistent id are byte-identical (AC-4 applied within one tenant). |
| `POST /bookings/{id}/cancel` | Optional `{ reason }`. 200 carries the freed interval and who/when/why. 404 `BookingNotFound`, 422 `BookingNotCancellable`, 409 `ConcurrencyConflict`, 400 `ValidationFailed`. **Deliberately not idempotent** — a second call is 422, because there is an actor and a time to overwrite. |
| `POST /recurrence-rules/{id}/cancel` | Optional `{ reason }`. 200 carries `cancelledBookingIds` — the ids, not a count, so a client knows exactly what it can stop showing as booked. 404 `RecurrenceRuleNotFound`, 422 `RecurrenceRuleNotCancellable`, 400. Cancels **only occurrences with `EndsAtUtc > now`**; past ones survive. |

Three constraints to hold on to: `sort` is whitelisted server-side to
`startsAtUtc | createdAtUtc | status`, so a sort control can only ever offer
those; `from`/`to` must each carry a zone designator and `to > from`, or the
request is a 400; and `Booking.CanBeCancelled` is *not terminal* **and**
`EndsAtUtc > now`, which is the predicate the UI mirrors to decide whether a
cancel action appears at all.

One more constraint the merge makes load-bearing: **`status` takes one value,
not a set**, which is why the drawn/not-drawn table above is a rendering rule
rather than a query parameter.

#### The calls settled for this phase

Three were settled on 2026-09-17, before the phase was re-planned. One survives,
one is superseded and one is moot — recorded that way rather than deleted, since
which of them the re-plan actually invalidated is the useful part.

1. ~~**The design arrives before step 1.**~~ **Moot.** The My Bookings design
   was never provided and the screen is cancelled. The design this phase needs
   is the calendar's (§4), wanted before step 2 rather than step 1.
2. ~~**Upcoming by default**, the list sending `from = now` with a Past
   toggle.~~ **Superseded** — there is no list. The calendar's equivalent is
   that it opens on the current month/week and navigates from there, with the
   view and date in the URL (below, step 1a).
3. **`scope=Own` only; decision `0002`'s TenantAdmin reach still defers to
   Phase 6.** Unchanged by the merge, and the reasoning is unchanged with it:
   FR-4.4 is the member's own view, Phase 6's queue already has to send
   `scope=tenant`, and building the widening there means building it once, with
   the screen that needs it, rather than threading a role-conditional branch
   through every step here for a path nothing yet exercises. `userName` is
   mapped but not rendered in this phase. Confirmed live on 2026-09-18: a plain
   member sending `scope=tenant` gets **400 ValidationFailed**, so this is not
   merely unused surface — shipping it would break the screen.

Two more settled on 2026-09-18, with the merge. Both are written up in full
above rather than restated here: **which statuses the calendar draws** (and the
accepted cost of an unreachable cancellation record), and **the calendar
becoming the landing screen at `/calendar`** with `/my-bookings` removed.

#### Steps

1. **Wire types and services — done, 2026-09-18.** `booking.models.ts` gains the
   list, detail and
   both cancel response types, mirroring the DTOs exactly; `BookingsService`
   gains `list()`, `getById()` and `cancel()`; `RecurrenceRulesService` gains
   `cancel()`. No screen, so this step is reviewable as a contract on its own.
   Tests: an omitted filter is left off the URL entirely rather than sent as a
   default this file invented (decision `0015`), `from`/`to` go out
   zone-designated, `sort` can only carry a whitelisted value, and both cancel
   calls use `skipErrorToast` because their refusals are rendered in place.

   **Delivered, with every shape confirmed against the running API rather than
   read off the C# records alone.** 16 new vitest tests (572 total, 0 failed),
   `npx ng build` clean (the three SCSS warnings pre-date this step). Five
   things worth recording:

   - **`sort` is a typed union, not a `string`.** `BookingSort` is
     `BookingSortField | \`-${BookingSortField}\``, mirroring
     `SortOption.TryParse`'s own `name` / `-name` grammar, so a typo is a
     compile error rather than a 400 at runtime. `ListResourcesParams.sort`
     (Phase 1) is a bare `string`; this is the stricter version, and the live
     probe confirmed both halves — `sort=quantity` is a 400 naming the
     whitelist, `sort=-startsAtUtc` a 200.
   - **`userId` and `scope` are deliberately absent from `ListBookingsParams`**,
     per the settled call that decision `0002`'s reach is built once in Phase 6.
     Not merely unused surface: a plain member's token sending `scope=tenant`
     answers **400 ValidationFailed**, confirmed live, so shipping it would
     break the screen rather than sit idle.
   - **The `!== undefined` guard in `buildListParams` is load-bearing, and the
     failure it prevents was verified rather than assumed.** `?status=` (an
     empty string) model-binds to a null enum and answers **200 with every
     booking** — a silently widened query, not an error. A truthiness check
     would have produced exactly that on a cleared filter control.
   - **A real `Withdrawn` approval exists in the dev database and reads oddly on
     purpose**: `decision: "Withdrawn"` with `decidedAtUtc` set and
     `decidedByUserId` **null** — there is no decider, only a fact
     (`ApprovalRequest.Withdraw`). `BookingDetailApproval` types the two
     independently for that reason. It is what cancelling a `Pending` booking
     produces, which is this phase's own step 5.
   - **Both cancels were exercised end to end and are non-idempotent as
     documented**: a second `POST /bookings/{id}/cancel` answers `422
     BookingNotCancellable`, a second series cancel `422
     RecurrenceRuleNotCancellable`. The series cancel returned
     `cancelledBookingIds` with all three occurrences' ids, which is what step 6
     reports from rather than a bare success.

   Everything created for the probes was cancelled afterwards — one one-off and
   a three-occurrence weekly series, all on Conference Room A in November 2026 —
   so the dev database carries only cancelled rows from this step. The five live
   bookings in it are the owner's own and were not touched.

   **Predates the re-plan**, and is the only step that does. It needed no
   revision: it was always a contract rather than a screen, which is why it was
   taken as its own step in the first place.

2. **The calendar shell and the bounded fetch — done, 2026-09-18.**
   `/calendar` replaces WP-6's placeholder and takes the landing slot
   (`/home` redirects, `''` points there, the nav item becomes "Calendar", the
   route title with it). A custom month/week grid — **no new dependency**, per
   §3's settled call.
   **The fetch strategy is the point, and it is what this step is really for**:
   `GET /bookings` bounded to the visible date window, re-fetched on
   navigation, never "fetch everything and filter in the browser". The one
   client-side filter is the status rule above, applied to a window that was
   already bounded — which is a rendering decision, not a fetch one.
   The current view and date live in the URL (`?view=&date=`), the same rule
   Phase 2's date navigator and Phase 3's selected slot both follow, so a month
   is shareable and survives a reload.
   **No client-side recurrence expansion, ever** — decision `0007` materialized
   every occurrence as its own `Booking` row, so a series is just rows that
   share a `recurrenceRuleId`. This is what makes the hard problem tractable
   and it must not be quietly reintroduced.
   Loading, empty and error states land here rather than being retrofitted:
   this is the first screen anyone sees after signing in.
   **Screens needed:** the calendar design (§4), wanted before this step.

   **Delivered, against both designs, which landed before the step started** —
   `design/calendar_month_design.png` and `design/calendar_week_design.png`.
   59 new vitest tests (631 total, 0 failed), `npx ng build` clean. The grid
   math lives in `calendar-range.ts`, kept out of the component the way
   `availability-grid.ts` is.

   **The wiring, which was the larger half of this step:** `/calendar` is the
   landing route, `/home` and `''` redirect to it, `my-bookings` is deleted,
   the nav item is "Calendar" (the `home` and `bookings` icons went with their
   items rather than staying as unreachable template branches), and
   `approverGuard` bounces to `/calendar` rather than through `/home`'s
   redirect, so the URL it names is the one the visitor lands on. The three
   booking-screen links were repointed and **the third was rewritten**, as
   flagged: it now carries `?view=week&date=…` for a one-off and
   `?view=month&date=…` for a series, because "check My Bookings" only worked
   when a new booking would be at the top of a list.

   **Deviations from the designs, each deliberate.** The owner's instruction
   was to follow them closely but adapt anything that disagrees with the app's
   own conventions, since they were generated without full knowledge of it:
   - **The sidebar drops "Home" and "My Bookings"**, which both designs still
     show. That is this phase's own decision, not a design question — Home
     redirects here and would be a second link to the same page, and My
     Bookings was cancelled. The designs also omit "Approvals", which this app
     renders for an eligible approver (decision `0018`).
   - **The breadcrumb reads "Calendar", not "Home > Calendar".** This app's
     breadcrumb has been the matched route-title chain since WP-6 and no
     screen shows a Home crumb; Phase 3 step 2 already flagged the same
     difference in the booking design. An app-wide breadcrumb change is not
     this step's to make.
   - **The week view's hour axis is data-driven, not fixed at 08:00–18:00.**
     The design's hours are the *default*; a booking outside them — which a
     viewer in a different timezone from the resource is enough to produce —
     would otherwise be drawn outside the grid and so be invisible. The window
     widens to contain whatever the week actually holds, which is the one
     place a taller grid beats a tidier one.
   - **A chip shows the booking's title, falling back to the resource name**,
     which is what both designs actually depict (a mix of "Weekly planning"
     and "Conference Room A"). An untitled booking is legal, so the fallback
     is the common case rather than the exception.
   - **The designs carry no loading, empty or error state**; all three are
     built. The empty state says "Nothing booked in this month", never "you
     have no bookings" — the fetch is bounded to the visible window, so the
     screen genuinely does not know about bookings outside it, and the grid
     stays drawn behind the notice because an empty September is still
     September.

   **Two things worth knowing before touching this file.** The visible month is
   as many whole weeks as it needs rather than a fixed six (September 2026 is
   five rows, exactly as the design shows), and **the window is walked across
   pages**: `PagingDefaults.MaxPageSize` is 100 and rejects anything larger
   rather than clamping, so a window holding more than one page is followed to
   the end. That is still a bounded fetch — the bound is the visible range.

   **A bug the owner found by looking, fixed the same day.** Week-view chips sat
   progressively below their own stated times — two compounding one-row errors:
   the grid drew a row per *label* (eleven for 08:00–18:00) while chip offsets
   were percentages of the ten-hour *span*, and `.hour-line` was a
   `border-bottom` sitting an hour under its own label. **No existing assertion
   could have caught it** — `top: 20%` is the string both the correct and the
   broken version emit, and jsdom does no layout — which is the availability
   screen's `<select [value]>` lesson in a new shape. Fixed by making
   `minuteOffsetPercent` the single place a time becomes a vertical position,
   with labels and chips both resolved through it; the grid now draws one row
   per hour span (`hourRows()`, one shorter than `hourTicks()`) and lines are
   `border-top`. Both regression tests were verified to fail against the
   pre-fix template and nothing else did. 635 tests.

   **Two more, reported with a screenshot the same day** — and both the same
   mistake: the day header and the columns were two grids in two different
   boxes, only one of which scrolled. The scrollbar lives *inside* the
   scrolling box, so the body's columns came out narrower than the header's and
   drifted ~17px by Sunday; and the 08:00 label, centred on the body's top
   edge, was half outside it and clipped, so no scroll position could reveal it.
   Fixed by making `.grid` the single scroll container for both views with the
   day header `position: sticky` inside it — the two grids are then the same
   width by construction rather than by compensating for a scrollbar width that
   is neither known nor constant — plus a symmetric `padding-top` on
   `.week-body` and `flex: none` on every direct child (a flex item shrinks to
   fit, which would leave nothing to scroll). **Testable after all**: jsdom does
   no layout but does resolve the component stylesheet, checked by probe before
   writing anything, so five new tests assert the mechanism and all five were
   proven to fail against the pre-fix CSS. 640 tests.

   **Verified against the running API**, not only by mocks: the exact request
   the component builds for September 2026
   (`from=2026-08-30T22:00:00Z&to=2026-10-04T22:00:00Z&page=1&pageSize=100&sort=startsAtUtc`)
   answered `200` with 8 rows — the owner's 5 live bookings, which the calendar
   draws, and 3 `Cancelled` ones, which the status rule drops. Both halves of
   the title/resource-name fallback appear in that response.

3. **Chips: what a booking looks like in a cell — done, 2026-09-18.** The status rules above, made
   visible — `Confirmed` plain, `Pending` distinct (and distinct by more than
   colour), `Completed`/`NoShow` muted, `Cancelled`/`Rejected` not drawn at
   all. A series occurrence carries a recurrence marker, exactly as the old
   list row was to have done.
   **Volume is proven here, not assumed**: a day with many bookings gets an
   overflow affordance ("+N more") rather than an unbounded stack, and the
   whole screen is checked against a realistic volume before the step closes —
   WP-3 Phase 5's 260-booking fixture is the existing benchmark to reuse or
   extend. This is the acceptance criterion "the calendar stays responsive
   under realistic data volume", so it is measured rather than eyeballed.

   **Delivered.** 18 new vitest tests (658 total, 0 failed), build clean.

   **Status is never carried by colour alone.** `Pending` takes a dashed outline
   *and* gains "(Pending)" in its own label — the design's own treatment, and the
   one distinction a member acts on, since FR-7.1 means the slot is not held yet.
   `NoShow` likewise gains "(No-show)", because that is information rather than
   decoration. `Completed` is muted but unannotated: it is the unremarkable past
   and there is nothing to do about it. Each chip also carries an `aria-label`
   giving the whole thing as one sentence — time, label, status, whether it is
   part of a series, and whether it is a clipped piece of a longer booking —
   since the visual version splits across four elements that read badly
   announced separately.

   **The overflow affordance expands the day in place**, which is a decision
   rather than a detail: the design shows "+2 more" but not what it does, and
   there is no day view to send anyone to, so expanding is what makes the capped
   chips reachable at all. The expansion is **not** in the URL — it is a
   disclosure inside one cell, not cross-screen state — and it clears whenever
   the window changes, since the cells it referred to no longer exist.
   The summary row takes a chip's *place* rather than being added below the full
   set; otherwise a capped four-booking day would be exactly as tall as an
   uncapped one and the cap would buy nothing on the day it matters.

   **No cap in the week view**, deliberately: a week chip is positioned by time
   rather than stacked, so the DOM is already bounded by what can physically fit
   in a day, and hiding one would leave a gap in the grid rather than a tidier
   list.

   **The responsiveness criterion was measured, not asserted.** Rendering the
   month view against increasing volumes (jsdom, so indicative rather than a
   browser figure):

   | Bookings in the window | Render | Chips in the DOM |
   |---|---|---|
   | 50 | 19 ms | 50 |
   | 260 (the benchmark) | 21 ms | 56 |
   | 500 | 41 ms | 56 |
   | 1000 | 64 ms | 56 |

   **The chip count plateaus at 56 while the data grows twentyfold** — that is
   the cap working, and it is the property the suite asserts (35 cells × at most
   3 chips) rather than a timing threshold, which would be flaky in CI and would
   not say *why*. The residual growth is the single O(n) pass laying rows into
   cells, which is unavoidable and cheap. Taken with step 2's bounded fetch, the
   cost of a month is flat in how much history the member has.

   **Verified against the running API**: a real three-occurrence weekly series
   was created on Conference Room A, confirmed to come back with
   `recurrenceRuleId` set on every occurrence (which is the only thing the
   marker keys off), and cancelled afterwards — the dev database is back to the
   owner's five live bookings. The `Pending` path needs no fixture: three of
   those five are already Pending.

   **Two more week-view bugs, reported with a screenshot the same day.** The
   report was "the cards aren't shown fully at the bottom"; the screenshot
   showed a second problem beside it.
   - **A short booking's chip was shorter than its own content.** Rows are a
     fixed 56px per hour, so a 30-minute booking is 28px, while the chip stacks
     a time line above a label line (~40px) — and `.week-chip` is
     `overflow: hidden`, so the booking's *name* was swallowed.
     `isCompactChip` now gives anything under 45 minutes a one-line layout. The
     threshold is a duration rather than a pixel measurement precisely because
     the row height is fixed.
   - **Overlapping bookings were painted on top of one another**, every chip
     having spanned the full column. `layOutDay` packs a day into side-by-side
     columns by the standard interval-graph sweep — clusters of transitively
     overlapping bookings, first free column within a cluster, the count taken
     per cluster so a crowded morning does not narrow the afternoon's lone
     booking, and a freed column reused rather than the day growing one per
     booking. Touching is not overlapping.
   Three of the four new DOM tests were proven to fail against the pre-fix
   template and stylesheet; the fourth (a full-hour booking keeping its two-line
   shape) is a guard on the threshold rather than a regression test. 670 tests.

4. **Booking detail — done, 2026-09-18.** `/bookings/:id` — its own route, not a panel, because
   FR-5.2 asks that each occurrence be independently viewable and because a
   booking worth discussing is worth linking to. Reached by clicking a chip.
   The span renders in the viewer's own zone with the resource's alongside when
   the two differ — the booking screen's convention, and §3's display default
   (decision `0003` governs the availability *question*, not how a booked
   instant is read back).
   Two cancellation cases still have to read correctly rather than as one
   generic "Cancelled", even though neither is reachable from a chip any more:
   `cancelledByUserId ≠ userId` is *cancelled by an administrator* (decision
   `0002`), and `cancelledByUserId = null` with a reason is *a blackout*
   (decision `0019`'s snapshot, which `Booking.CancelForBlackout` leaves the
   actor null for on purpose). They remain because a **direct link still
   resolves** — the booking screen's own links, a bookmark, an email — so the
   screen must render a cancelled booking honestly even though the calendar
   will not route anyone to one. A 404 reuses the established "doesn't exist,
   or you don't have access" wording.

   **Delivered.** `features/booking/detail/`, 25 new vitest tests (695 total, 0
   failed), build clean. Calendar chips became real `<a>` elements pointing
   here, in both views — an anchor rather than a click handler, so middle-click,
   copy-link and open-in-new-tab all work, the same reasoning the 2026-09-16
   accessibility pass applied to the resource card's title.

   **The resource is a second, best-effort fetch, and its failure is
   deliberately silent.** `GetBookingQueryResponse` carries `resourceName` but
   no `timeZoneId`, so without it the screen cannot say what the span means on
   the room's own clock — but every other fact on the page is still true, so a
   failed resource read costs one line rather than the screen. Same reasoning as
   the availability screen's blackout fetch. It is also guarded on arrival: the
   `switchMap` covers the booking fetch only, so a slow resource read for a
   previous booking is dropped rather than landing on a newer one.

   **The viewer's zone leads here, the opposite emphasis from the booking form
   one screen back** — and deliberately so. The form led with the resource's
   zone because decision `0003` makes that the zone the availability question
   was asked in, and the member chose against that reading. Reading a booking
   *back* is the ordinary calendar case (§3), where what a person wants to know
   is when to actually turn up.

   **`spanLabels`/`instantLabel` moved to `local-date.ts` at their second
   caller** rather than the usual third, for the reason `formatDurationWords`
   moved at its second: they are user-visible copy rendering the *same
   booking's* span on two screens in one flow, so a second copy that drifted
   would be a visible inconsistency rather than merely duplicated code.

   **Verified against the running API**, every branch against a real row:
   - a `Pending` booking with `approval.decision: "Pending"` and a real
     `expiresAtUtc` — the FR-7.1 "not held yet" lead and the expiry row;
   - a self-cancelled booking where `cancelledByUserId === userId`;
   - a row that exercises three branches at once — `Cancelled`, a `Withdrawn`
     approval with a null decider, and `recurrenceRuleId` set — which is what
     cancelling a `Pending` occurrence of a series actually produces;
   - `404 BookingNotFound` for a real-but-nonexistent guid, which is the
     not-found state, and a non-guid path segment that never matches the
     backend route and answers 404 the same way.

   **One flagged edge, not handled**: an all-zeros guid
   (`/bookings/00000000-0000-0000-0000-000000000000`) answers **400
   ValidationFailed** rather than 404, because `CancelBookingCommandRequest`'s
   sibling validator treats `Guid.Empty` as a malformed request rather than a
   lookup that missed. It therefore lands in the generic error state with a
   retry that cannot help. Only reachable by hand-typing that exact id, so it is
   recorded here rather than given a special case.

5. **Cancel one booking.** Confirm-then-act, with an optional reason.
   `CanBeCancelled` is mirrored client-side to decide whether the action shows
   (not terminal **and** `EndsAtUtc > now`); the server stays the authority.
   Refusals go through a cancel dialect on the `RejectionDialect` the
   2026-09-17 hardening pass introduced — `BookingNotFound`,
   `BookingNotCancellable`, `ConcurrencyConflict` — rather than a second mapper.
   **Because cancel is not idempotent it inherits `POST /bookings`' rule
   exactly**: disabled while in flight, and *no retry button* on an unknown
   outcome, since a repeat would quietly rewrite who called the meeting off.
   Success updates from the response rather than blind-refetching — the
   response carries the freed interval precisely so it can.
   **And the chip disappears**, which is this phase's own status rule doing its
   job: a cancelled booking is not drawn, so the calendar must drop it from the
   window it is already holding rather than re-querying.

6. **The series choice.** A booking carrying a `recurrenceRuleId` offers an
   explicit two-way choice — *this occurrence* or *the whole remaining series* —
   never one button that is ambiguous about which it means. The copy states what
   "remaining" means (`EndsAtUtc > now`; past occurrences survive) **before** the
   member confirms, not after, and the result reports how many occurrences were
   actually freed from `cancelledBookingIds` rather than a bare success. Those
   ids are also exactly what the calendar removes from view, which is why the
   endpoint returns them rather than a count.

7. **Sweep.** DOM assertions for every state, not signal-level ones — Phase 3's
   own lesson, and both of its bugs were things a member could see.
   Accessibility: status conveyed by more than colour (which the `Pending`
   distinction depends on), focus handled on the confirm affordance, 400px
   width. Then the live walkthrough in the Demo line below, and the
   roadmap/CLAUDE.md updates at close.

#### Flagged before starting

- **A cancelled booking becomes unreachable in the UI**, by design (the status
  table above). The email carries the fact; the reason — including decision
  `0019`'s blackout snapshot — is readable only by direct link to
  `/bookings/:id`. Accepted by the owner on 2026-09-18, recorded here rather
  than discovered at review.
- **This app has no modal primitive.** The cancel confirmation is planned as an
  inline expanding panel rather than a dialog: a focus-trapped modal is a real
  component with real accessibility obligations, and nothing else in this phase
  needs one. Revisit if the design asks for a true dialog.
- **A month grid at 400px is the open design question**, and it usually
  degrades into an agenda list — which is, not coincidentally, the shape of the
  list this phase just deleted. That is not a reason to keep the list screen;
  it is a reason for the calendar design (§4) to say what narrow width looks
  like, rather than leaving step 7 to invent it under an accessibility
  checkbox.
- **No new numbered decision docs are expected**, matching Phases 1–3: the
  calls above live here, beside the step they govern, unless one starts being
  cited from outside WP-7.

**Demo:** navigate several months on a tenant carrying a realistic booking
volume with no visible jank, and see a multi-week recurring series render
correctly across a month boundary; then cancel a one-off booking and confirm
both that its chip disappears and that the slot frees in Phase 2's availability
view; cancel one occurrence of a series and confirm the rest survive; cancel
the whole series and confirm every future occurrence goes with it while past
ones stay.

**API:** `GET /bookings` (date-window bounded), `GET /bookings/{id}`,
`POST /bookings/{id}/cancel`, `POST /recurrence-rules/{id}/cancel`.

### ~~Phase 5 — Calendar view (the hard problem)~~ — absorbed into Phase 4

**Merged into Phase 4 on 2026-09-18** (owner's call; the reasoning is in Phase
4's own preamble). Every bullet that stood here now lives in Phase 4's steps 2
and 3 — the custom grid with no new dependency, the date-window-bounded fetch,
the standing prohibition on client-side recurrence expansion (decision `0007`),
the "+N more" overflow affordance, and the 260-booking responsiveness benchmark.
Nothing was dropped in the move.

**The number is retired rather than reused.** Phases 6 and 7 keep theirs, so
every existing reference to "Phase 5, the hard problem" — in
[`docs/roadmap/wp7.md`](roadmap/wp7.md), in CLAUDE.md, in this file's earlier
sections, and in the commit history — still resolves to something true. A gap in
the sequence is cheaper than a renumber that falsifies the documents citing it.

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
  real navigation (resource → availability → book → **calendar** → booking
  detail → cancel; approver flow via the nav's Approvals item) rather than only
  demoed in isolation. *The chain used to read "→ my bookings → cancel"; it
  changed with the 2026-09-18 merge, not because the coverage changed.*
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

**Restructured 2026-09-18** (owner's instruction, between Phase 4's steps 4 and
5). Every feature is now split by *what a file is* rather than holding a flat
list of them:

```
frontend/src/app/
  tests/                     app.routes.spec.ts, app.spec.ts
  core/                      auth/ · http/ · notifications/, each with tests/
  features/<feature>/
    components/
      <component>/           one folder per component, its three files and
                             nothing else
    services/                the thin API services
    models/                  the wire types
    <purpose>/               one folder per kind of helper — see below
    tests/                   every .spec.ts for the feature
  layout/
    components/shell/        shell.component.ts|html|scss
    breadcrumb.service.ts
    tests/
  shared/                    brand-mark/ · resource-type/
```

A component's folder is named for the component without the `.component`
suffix — `booking/`, `booking-detail/`, `resource-list/` — so the folder reads
as the thing and the files inside keep the Angular naming the CLI and every
convention already expect. Because the three files travel together,
`templateUrl`/`styleUrl` stay `./<name>.component.html` and never needed
touching.

The per-feature helper folders, named for what they do rather than for the
screen that happens to call them:

| Feature | Helper folders |
|---|---|
| `availability/` | `grid/` (`availability-grid.ts`), `date/` (`local-date.ts`) |
| `booking/` | `arrival/` (the slot + mode URL contract), `rejection/` (reason code → message *and* placement, both dialects), `recurrence/` (form guards, request builder, outcome shaping) |
| `calendar/` | `grid/` (`calendar-range.ts` — URL contract, boundaries, fetch window, layout) |

**Two rules hold everywhere, not just in `features/`:** a component lives in its
own folder under `components/` with only its own three files, and **no
`.spec.ts` sits outside a `tests/` folder**. The second is absolute — the
owner's instruction was that a spec should never be visible without opening a
tests folder first. `core/` and `shared/` keep their existing internal shape
(they already group by concern) and gained only the tests folders;
`core/notifications/` is the one place a component still sits beside a service,
and extending the rule to it is a two-minute change if wanted.

The vitest config needed no change: it globs `src/**/*.spec.ts`, so where a spec
lives was never part of the contract.

**`my-bookings/` was in this list and is now gone** — the screen was cancelled
on 2026-09-18 and the folder was never created, so nothing had to be moved. The
booking *detail* screen lands in `booking/components/` beside the form rather
than in a folder of its own: it reads the same aggregate through the same
`BookingsService` and the same `booking.models.ts`, which is where step 1
already put its types.

Each feature gets its own thin API service (`resources.service.ts`,
`bookings.service.ts`, `recurrence-rules.service.ts`,
`availability.service.ts`) rather than one large API client — mirroring the
backend's per-feature-folder convention (decision `0015`) rather than
inventing a different shape on the frontend. The calendar is the one screen
that will read through a service owned by *another* feature (`booking/`'s), for
the same reason `booking-arrival.ts` sits in `booking/` while the availability
screen imports it: the contract belongs with the aggregate, not with whichever
screen happens to render it.

---

## 7. Notes

- **Flagged gap, not silently dropped**: resource admin CRUD (create, edit,
  archive, availability-window/approver/blackout management UI). The designs
  already assume it and the backend has supported all of it since WP-3, but
  it is not in WP-7's task list. Needs its own subsection when a future work
  package picks it up; until then the buttons stay absent from the UI rather
  than pointing at nothing.
- **Flagged gap, with a named owner: `POST /bookings` has no idempotency
  key.** Found while planning Phase 3 (2026-09-16). `POST /recurrence-rules`
  gained one in the 2026-09-15 hardening pass (item 11,
  `RecurrenceCreationOperation` + an `Idempotency-Key` header); the single-
  booking endpoint never did. The consequence is concrete: a one-off booking
  whose response is lost in transit cannot be safely retried — a second
  attempt creates a second booking, and `dbo.CreateBooking` is right to allow
  it, since two legitimately distinct bookings of the same slot on a pooled
  resource are a real thing. Owner's call (2026-09-16): **not fixed inside
  WP-7**, because a frontend package does not patch the backend (the rule
  directly above this list). Phase 3 mitigates what the client can — no
  auto-retry on a one-off submit, and a transport failure that says the
  booking *may* have been created and links the member to check, rather than
  a retry button that could double it. **The fix belongs to whichever backend
  work package comes after WP-7**: mirror the recurrence approach
  (an operation record keyed on `(OrgId, UserId, IdempotencyKey)`, a header
  on `BookingsController.Create`, resolution to the same `Booking` on a
  repeat), at which point Phase 3's mitigation is replaced by a real retry.
- Each phase's own section above is the outline; the actual step-by-step
  breakdown (the granularity WP-3 through WP-6 used for review checkpoints)
  is added to this document **one phase at a time, immediately before that
  phase starts** — not drafted for every phase up front, per the owner's
  instruction for this package. (Originally "all seven phases"; Phase 4
  absorbed Phase 5 on 2026-09-18, so there are six live phases and a retired
  number.)
- **Carried out of Phase 3, updated for the 2026-09-18 merge:**
  - ~~`/my-bookings` is still WP-6's placeholder, and two shipped screens
    already link to it.~~ **Superseded.** The route is being *removed*, not
    given a destination, and there are **three** links into it rather than two
    (`booking.component.html`, currently lines 152, 260 and 725) plus the nav
    item in `shell.component.ts`. All four repoint to `/calendar`; the third
    link needs rewriting rather than repointing, for the reason set out in
    Phase 4's landing-screen section. None is dead in the meantime — they point
    at WP-6's placeholder, which still renders.
  - ~~The booking screen's own `BookingsService` has `create()` only.~~ **Done
    2026-09-18**, in step 1: `list()`, `getById()`, `cancel()` and
    `RecurrenceRulesService.cancel()` all exist and are tested against the live
    API. The re-plan did not touch them.
  - `booking-rejection.ts` deliberately maps only the codes
    `POST /bookings` can return. Phase 4's cancel path brings
    `BookingNotFound` and `BookingNotCancellable`, which belong in that same
    catalogue with the same "message *and* placement" treatment rather than in
    a second one. **Unchanged by the merge** — still step 5's job.
- Nothing here touches the backend. If a phase turns up a genuine contract
  gap (a field the UI needs that no response carries, an endpoint shape that
  doesn't fit the screen), that's a stop-and-ask per CLAUDE.md §11, not a
  silent backend patch mid-frontend-WP — same rule WP-6 closed with.
