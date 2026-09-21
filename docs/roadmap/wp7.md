_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-7 — Booking UI & Calendar — **In progress**
Source: `docs/Work Packages - Week 5 and 6.pdf` (weeks 5–6, frontend track),
which carries WP-6 and WP-7 together.
**Plan: [`docs/wp7-plan.md`](docs/wp7-plan.md)**, approved 2026-09-15 before
any code was written, the same process every prior WP has gone through. The
owner has allocated more than a week to this package and asked for every
phase to be built seriously and split into its own reviewable steps, rather
than compressed — mirrored in wp7-plan.md's own phasing.

- [x] Resource list and detail views. **Done 2026-09-15** (Phase 1).
- [x] Availability view for a resource and date range. **Done 2026-09-16**
      (Phase 2).
- [x] Booking form for one-off and recurring bookings, with clear validation
      feedback. **Done 2026-09-17** (Phase 3).
- [ ] Calendar view rendering bookings, including recurring series, without
      choking on volume. **In progress** as Phase 4 since the 2026-09-18
      re-plan.
- [ ] Approval queue UI for approvers.
- [ ] Cancellation and blackout handling in the UI. **Phase 4's steps 4–6.**
- [ ] Wire the full flow end-to-end against the real API.

**Hard problem**: the calendar must stay responsive with hundreds of bookings
and expanded recurring series. It was Phase 5 until 2026-09-18, when Phases 4
and 5 merged — see this file's own Phase 4 section, below.

Acceptance criteria:
- [ ] A member completes browse → book → confirm entirely through the UI.
- [ ] Recurring bookings render correctly in the calendar.
- [ ] The calendar stays responsive under realistic data volume.
- [ ] An approver can action pending requests from the UI.

**Phase 1 (resource list & detail) is done, 2026-09-15**, in five steps —
full detail in `docs/wp7-plan.md`. `ResourcesService` (`list()`/`getById()`);
`features/resources/list/` (the browse screen: type pills, search, an
"Approval" filter, an "Include archived" toggle behind "More filters", a
truncation notice past the 100-row fetch ceiling) and
`features/resources/detail/` (resource info, booking rules, the bookable-
hours table built from `AvailabilityWindows`, a distinct 404 state). Neither
screen renders the admin actions (`New resource`, `Edit resource`) the
provided designs show — deliberately out of scope, flagged rather than
silently dropped (wp7-plan.md §7). Routes landed incrementally
(`/resources`, `/resources/:id`, `/resources/:id/availability` — the last
still a placeholder for Phase 2) rather than all at once in a final wiring
step, following WP-6's own precedent of swapping one placeholder at a time.
89 vitest tests pass, 0 failed.

**A mid-phase reversal, not a mistake left in**: step 3's search box and
"Approval" filter were first built as client-side predicates over one
fetched page, because `GET /resources` had never supported either. The
owner then asked for the backend to be extended properly instead — see this
file's own "Resource list filters extended for WP-7" entry, below — and step
3 was redone against the real `search`/`requiresApproval` query params, with
the search box debounced (500ms) since every keystroke
now costs a real HTTP round-trip. The redo's own regression test
(`ignores a stale search response that resolves after a newer search has
already landed`) proves the existing `latestRequestId` stale-response guard
— built for the type-pill case — covers this race unmodified, since every
trigger on this screen shares one `load()`.

**Two things this phase found and fixed that weren't in its own task
list**: `ShellComponent.breadcrumb` had been a flat `[title()]` since WP-6,
with a comment flagging that real route nesting (added this phase,
`resources` → `:id`) would need it to walk every matched level — done here,
plus a new `layout/breadcrumb.service.ts` (`BreadcrumbService`) letting the
detail page override the last crumb with the loaded resource's own name
(`Resources > Conference Room A`), cleared on destroy. And
`ResourcesService`'s two calls never opted out of the global error toast, so
this screen's own inline error/retry states would have doubled up with a
redundant one — fixed with `skipErrorToast()`, the same reasoning
`AuthService.login()`/`.logout()` already apply to theirs.

**Verification gap, flagged rather than glossed over**: no browser-
automation tool was available this session, so the click-through
walkthrough wp7-plan.md's own step 5 calls for (log in, browse, filter, open
a resource) was **not performed**. In its place, the real running backend's
contract was smoke-checked directly via `curl` against every request shape
the frontend sends (list filters individually and combined, the detail
read, the 404 path) — confirming the shape the unit tests mock is the real
one, but not a substitute for seeing the screens actually render. Recommended
before treating Phase 1 as fully signed off.

Notes:
- `docs/wp7-plan.md`'s own §7 records the one deliberately-out-of-scope
  gap (resource admin CRUD) and the browser-local-time display default;
  neither changed during Phase 1.
- The type-icon SVG mapping and type-label strings were duplicated between
  the list and detail components — only the second occurrence at the time
  (`BrandMarkComponent`'s own precedent extracts on the third). Resolved in
  Phase 2 step 2, below, once the availability screen became the third
  caller, not deferred to Phase 3 as this note originally guessed.

**Phase 2 (availability view) is done, 2026-09-16**, in seven steps — full
step-by-step detail in `docs/wp7-plan.md`. `AvailabilityService` (one
`get()` against `GET /resources/{id}/availability`); `features/availability/`
(the resource summary card, a resource-local date-range navigator with a
custom-range popover, a quantity stepper for pooled resources, and a custom
hour-axis grid — no calendar library, per `wp7-plan.md` §3's settled call —
rendering bookable bars the viewer can click, narrow via Start/End dropdowns
or drag, and carry forward to booking). `RESOURCE_TYPE_LABELS`/the type-icon
SVG switch, flagged as duplicated after Phase 1, were extracted this phase
(the third occurrence, per `BrandMarkComponent`'s own precedent) into
`shared/resource-type/`. Route landed on `:id/availability` (replacing
Phase 1's placeholder) plus a new `:id/book` placeholder for Phase 3.
199 vitest tests pass, 0 failed.

**The grid's own math lives in `features/availability/availability-grid.ts`
and `local-date.ts`, kept out of the component since none of it is
Angular-specific.** The hard part: `BookableIntervalDetail` carries UTC
instants, but the grid draws in the *resource's* local time (decision
`0003`), and an interval can in principle span a local midnight (decision
`0022`'s "`ClosesAt = 23:59:59` means the following midnight" chaining into
the next day's own opening window) — so `splitIntervalByLocalDay` clips each
interval into one `DaySegment` per local day it touches. Selecting a
sub-range within a segment needed a *precise* UTC instant for "Continue to
booking," which only the original interval carried — solved by giving each
`DaySegment` its own `startUtc`/`endUtc`, walked forward from the interval's
own `startUtc` by local minutes elapsed (`addMinutesToUtc`) rather than a
full local-to-UTC converter, which would have duplicated DST-transition
policy that's deliberately backend-only (§4.3). Exact for every resource in
this app's seed data (none span a DST transition) and for any ordinary
business-hours resource; the one case it doesn't perfectly cover (a DST
transition landing inside an overnight multi-day segment) is documented in
`DaySegment`'s own comment rather than hidden, and backstopped by
`dbo.CreateBooking`'s own re-validation under lock at actual submission
time — a wrong pre-fill here is a UX rough edge, never a double-booking risk.

**Selection went through four rounds of owner-driven refinement after the
first pass, each landed as its own reviewable increment:**
1. Min/max booking duration wasn't enforced at all in the first pass
   (deliberately deferred to Phase 3, per the original plan) — the owner
   asked for it in the availability screen itself. `startTimeOptions`/
   `endTimeOptions` are now bounded by the resource's own
   `minDurationMinutes`/`maxDurationMinutes` (a `null` minimum still
   requires one 15-minute step; a `null` maximum is genuinely unbounded), a
   `durationError` computed catches the one case dropdown bounds can't
   route around (a segment itself shorter than the resource's minimum), and
   the default selection on a click changed from "the whole segment" to
   "segment start through the resource's own max duration" — never itself
   invalid, where selecting the whole segment sometimes was.
2. The clicked bar originally turned solid burgundy, hiding which part of
   it was actually selected — the bar now stays green with a thin outline
   marking "this is the active bar," and a separate `.selection-overlay`
   shows the exact Start-to-End sub-range on top, with two draggable
   circular handles at its edges (the Pointer Capture API —
   `setPointerCapture` — rather than document-level mousemove/mouseup
   listeners, covering mouse/touch/pen alike with no manual teardown).
3. The overlay's whole body is now draggable too (not just its edges),
   translating both Start and End together by the same 15-minute-snapped
   delta, clamped to the segment's own bounds, preserving the selected
   duration exactly.
4. Clicking anywhere outside the active selection's own UI (a different bar
   aside — that already reselects) now clears it, the same as the explicit
   "Clear selection" link, via the same `document:click` listener already
   watching for the range popover's own outside-click dismissal.

**Three owner-requested changes on 2026-09-17, during WP-7 Phase 3.** (1)
Selecting a bar low in the grid no longer leaves it out of view — the
selection panel appearing below shrinks the scroll box from the bottom while
`scrollTop` stays, so `selectSegment` now scrolls the clicked bar back into
view (`block: 'nearest'`, in `afterNextRender`, once the panel has taken its
space). (2) The gaps between bars are accounted for by item 3's labelled pills — a
full-width silver track was built first and removed the same day as not what
was asked for. (3) Unbookable time inside opening hours is now labelled
"Unavailable" (a blackout) or "Booked" (anything else) — derived entirely
client-side from `ResourceDetail.availabilityWindows` and
`GET /resources/{id}/blackout-periods` (a `TenantMember` read by design), so
no backend change was needed, which was the owner's own condition. One pill
per continuous reason: touching/overlapping opening windows merge before
anything is subtracted (the dev database's 3D Printer has two abutting
weekday windows, which was splitting one blackout into two pills), and the
result is merged again by kind. Full detail in `docs/wp7-plan.md`'s Phase 2
step 6.

**A display bug in this screen's Start/End dropdowns, found by the owner on
2026-09-17 during WP-7 Phase 3 and fixed the same day.** `<select [value]>`
with `@for`-rendered options: the binding sets the value property once, a
single select resets to its first option whenever its option list is rebuilt
(`endTimeOptions` depends on `selectedStartMinutes`, so constantly), and
Angular doesn't re-apply a binding whose value hasn't changed — so the End
dropdown showed Start + the resource's *minimum* while the real selection was
Start + its *maximum*. Fixed with `[selected]` per option plus a
`withSelectedOption` helper for a held value that falls between two steps.
Every existing test of this interaction asserted at the signal level and
passed throughout; the three regression tests added assert against the
rendered DOM and were confirmed to fail against the old template. Full detail
in `docs/wp7-plan.md`'s Phase 2 step 6.

**A genuine debugging detour, worth remembering for any future "pin the
chrome, scroll only this one region" screen** (the calendar, Phase 5, is a
likely next case): the owner's request that only the grid's rows scroll,
not the whole page, took three attempts. The first two used `height: 100%`
percentage sizing that silently did nothing, because percentage height only
resolves against a containing block with a *definite* height, and the
actual chain from the shell's `.content` down to `.grid-rows` had a plain
`display: block` link partway down (`AvailabilityComponent`'s own `:host`)
with no height rule at all — it sized to its own content instead of the
space available, so nothing below it was ever actually bounded. The fix
that worked made `.content` (shell) a flex column and `:host` a flex item
with `flex: 1; min-height: 0` — a *definite*-height chain via flexbox at
every link, not percentages hoping to resolve through however many
intermediate elements happen to be there. `npx ng build` succeeding at each
wrong attempt never caught this, because it only proves the CSS compiled,
not that the cascade does what's intended.

**Also raised the SCSS per-component style budget**
(`frontend/angular.json`, `anyComponentStyle.maximumError`: 8kB → 16kB,
warning left at 4kB) after hitting the old hard ceiling twice in a row on
this screen, once it grew a popover, a stepper, a grid, a selection overlay
and drag handles. A deliberate, flagged trade against continuing to trim
real functionality to fit an arbitrary WP-6-era number (one hover effect
was cut in an earlier round specifically to fit under it — not repeated).

**Owner question mid-phase, not a bug**: asked why Acme's two resources
showed no blackout gaps in the default 7-day view. A subagent queried the
live API directly, authenticated as `approver@acme.test` (row-level
security means a bare `sqlcmd` session sees nothing without
`sp_set_session_context`, so the API is the only fast way to check) and
confirmed both resources' seeded blackouts (3D Printer: Sep 10–11;
Conference Room A: Dec 25–26) simply fall outside the default Sep 16–22
window — nothing missing or broken. Also surfaced, as an aside: the WP-7
plan's own note about a seeded Pending 3D Printer booking (a Phase 6 demo
fixture) doesn't currently exist in the database. Flagged, not chased
further — out of scope for a read-only check, and not this phase's concern.

**Verification**: `npx ng test --watch=false` — 17 test files, 199 passed,
0 failed. `npx ng build` — clean (the SCSS budget warning only, comfortably
under the raised ceiling). Unlike Phase 1, the owner performed a live
click-through directly this time — browse a resource, pick/adjust a date
range, select and narrow a bookable bar (via dropdowns and both drag modes),
Continue to booking landing on the Phase 3 placeholder — and confirmed it
works, closing the verification gap Phase 1 had to leave open.

Notes:
- `resources/:id/book` exists now only as a placeholder; Phase 3 replaces
  it and has to honor the selected-slot contract Phase 2 already established:
  `{ startUtc, endUtc, quantity }`. **Amended 2026-09-17** (owner's decision,
  during Phase 3 step 2): that hand-over was router state and is now **query
  parameters** — `?startUtc=…&endUtc=…&quantity=…` — so a selected slot is
  shareable, bookmarkable and visible in the URL, which the History API's
  per-entry state never was. Both halves of the contract live in
  `features/booking/booking-arrival.ts`; `continueToBooking` is the only line
  of the availability screen it touched.
- The `.claude/skills/report-back/SKILL.md` end-of-task report format was
  revised twice mid-phase at the owner's request (summary now states
  what/why/how; only genuinely important code gets a per-file explanation,
  tests get one consolidated "what this batch proves" line instead of
  per-file ones, docs stay listed-only) — a process change, not a WP-7
  feature, but worth knowing since every report from here on follows it.

**Phase 3 (booking form) is done, 2026-09-17**, in eight steps — full
step-by-step detail in `docs/wp7-plan.md`. `features/booking/`: the two
services (`bookings.service.ts`, `recurrence-rules.service.ts`), the booking
screen itself, and four pure modules kept out of the component the way
`availability-grid.ts` is — `booking-arrival.ts` (the selected-slot and mode
URL contract), `booking-rejection.ts` (reason code → message *and* placement),
`recurrence-form.ts` (the recurring form's guards and its request builder) and
`recurrence-outcome.ts` (one shape for a 201 and an all-refused 422). Split
into two branches at the owner's own seam: the one-off half (steps 1–5) and the
recurring half (steps 6–8). 462 vitest tests pass, 0 failed.

**Three owner decisions reshaped the phase while it was being built**, each
recorded in full in `wp7-plan.md`:
1. **Query parameters, not router state** (step 2). The selected slot lives in
   the URL, so it is shareable, bookmarkable and visible. Router state survives
   a reload but nothing else.
2. **Editable recurring times, not read-only** (step 6). The owner proposed
   locking them, on the grounds that editing invalidates whatever the
   availability screen checked. The counter that won: a recurring series was
   never checked — the query answered one question about one slot, and FR-5.4's
   per-occurrence report exists precisely because partial refusal is the
   designed outcome. Locking would verify occurrence 1 of N and still not
   prevent the failure worth preventing, so `outsideOpeningHours` checks the
   entered time against the resource's own windows instead.
3. **The availability screen is no longer a toll booth** (step 6, second
   round). Requiring a picked slot made a recurring booker perform a ritual
   they had no use for. `?mode=recurring` became part of the URL contract, the
   resource list *and* detail screens link straight to it, and the form seeds
   itself from the resource's own schedule when no slot was picked.

**Two bugs the owner found by clicking, neither caught by the suite** — the
lesson recorded at the time: for anything the user *sees*, assert against the
rendered DOM, and prove the regression test fails against the old code.
1. The availability screen's Start/End dropdowns displayed the wrong time
   (`<select [value]>` with `@for` options; the browser resets a single select
   to its first option whenever the list is rebuilt, and Angular does not
   re-apply an unchanged binding). Fixed with `[selected]` per option plus
   `withSelectedOption`; three DOM regression tests, each verified to fail
   against the old template.
2. A hand-edited `?quantity=16` on a one-unit resource **booked one unit and
   reported success**, because the form clamped silently. Clamping was the
   wrong instinct — decision `0015` rejects an oversized `pageSize` rather than
   clamping it — so the value is now kept and refused, with the message placed
   where the member can act on it (against the stepper, or at the top of the
   form when an exclusive resource renders none).

**A documentation error that probing turned up** (step 5): CLAUDE.md §6
claimed an exclusive resource can only ever answer `SlotUnavailable`. The live
API answers `CapacityExceeded` for `quantity: 3` on a capacity-1 resource,
because `Quantity` is deliberately unbounded at the validator. Corrected in
§6 and in five other copies of the same sentence; the one inside the applied
migration was left alone per CLAUDE.md §5 and flagged instead.

**Verification**: `npx ng test --watch=false` — 26 files, 462 passed, 0
failed; `npx ng build` clean. All four of step 8's scenarios were exercised
against the running API with the exact request shapes the client builds —
`201 Pending` with its approval detail, `409 SlotUnavailable`, a mixed series
(`Created / Refused / Created`) forced by a blocking booking, and a `422
NoOccurrencesCreated` whose `occurrences` are structurally identical to the
201's. The idempotency key was proven live in step 7: re-posting an identical
body with the same key returned the same `recurrenceRuleId` and the same
booking ids. Everything created was cancelled afterwards. **The browser
walkthrough itself remains outstanding** — no automation is available here, so
what is verified is every request/response pair the screens depend on plus the
rendering assertions in vitest, not the rendered flow end to end.


---

## Recurring-booking hardening pass — 2026-09-17

Not a new phase: a focused review of the recurring half Phase 3 had just
landed (`feature/ui-bookings-recurrent`), against seven numbered findings.
Each was checked against the actual implementation, the backend contract it
consumes, and the existing frontend conventions before anything changed —
six were confirmed and fixed, one was answered with a decision rather than a
migration. Baseline afterwards: **556 vitest tests** (was 462), production
build clean.

**The one that was a crash, not a message (finding 1).** Every date helper in
`local-date.ts` is `Date` arithmetic underneath, and an invalid `Date` throws
only at `toISOString()` — so `addDays('', 7)` and `addDays(d, 7e18)` both raise
`RangeError: Invalid time value` rather than returning something odd.
`validateRecurrenceForm` derived the implied end date *before* checking the
primitives it derived from, and it ran inside a `computed` the template reads
on every keystroke. Two ordinary member actions reached it: **clearing the
Start date box** (an `<input type="date">` hands back `''`) and **typing a long
number** into Repeat every or After N occurrences. `Number.isInteger` was the
wrong guard for the second — `1e21` passes it, and its product with a count is
what gets multiplied into a `Date`. Fixed by ordering the validation
structurally (primitives first, derived arithmetic only over primitives that
passed), switching to `Number.isSafeInteger`, and restating the server's own
10,000-day pre-check (`IsOccurrenceCountWithinMaxSpan`) so a large pair is
refused by a day count rather than by `addDays` being handed it. A cleared
start date with the *end-date* arm selected produced **no error at all**
before this (`'2026-10-01' < ''` is false), so the form would submit a request
only a 400 could answer.

**A series read through the one-off form's vocabulary (finding 2).** The
recurring submit routed its failures through `describeBookingRejection`, which
knows three controls — title, quantity, duration — and treats every other named
field as *"the selected time is not valid. Go back to availability and pick a
slot again."* `POST /recurrence-rules` validates `StartDate`, `IntervalValue`,
`OccurrenceCount`, `EndDate`, `LocalStartTime`/`LocalEndTime`, `Quantity` and
`Title`, so most of its 400s pointed the member at a screen that feeds none of
those fields while the control that held the problem said nothing. Worse, a
`BookingDurationOutOfRange` for a series mapped to the `duration` field — whose
message renders inside the one-off panel that recurring mode hides, so it was
**invisible**. Fixed by splitting `booking-rejection.ts` into shared machinery
plus a `RejectionDialect`, with the recurring vocabulary in a new
`recurrence-rejection.ts`: its own field map, its own copy, and no
"re-check availability" action for a request that never had a slot. Each
recurring control now shows the server's message in preference to the client's
own, the same precedence `titleError`/`quantityError` already applied — and an
edit to a control clears the server message it carried, because a rejection is
otherwise only cleared *by* the submit it is blocking, which would have locked
the form permanently on any server-only rule.

**The form stayed editable mid-flight (finding 3).** Only the Confirm button
was disabled, so a member could switch Weekly to Monthly and 09:00 to 14:00
while the original request was in the air. Two consequences, both fixed:
the confirmation panel derived its description from the live form (a 201 for
the Weekly series was announced as a Monthly one), and the idempotency key's
promise silently lapsed — `createSeries` keeps a key only for the body it was
minted for, so an edit after an unobservable outcome meant the next submit
minted a *fresh* key while the screen still said "trying again is safe", which
could create a second series if the first had in fact committed. Now every
control that feeds the request is disabled while it is in flight (one
`[disabled]` on the `<fieldset>`, plus the title, the stepper and the mode
toggle), the panel is rendered from a snapshot of what was submitted, and
`canRetrySeries` is gated on the current form still building byte-identical
bytes to the pending attempt.

**"Series booked" for occurrences nobody had approved (finding 4).** The
handler creates each occurrence with `resource.RequiresApproval ? Pending :
Confirmed`, and the one-off panel one screen over already says a Pending
booking is "not held for you yet" — while the series panel said *Series
booked*, *2 booked*, and *Booked* against every date. Now worded off
`requiresApproval`: **Series submitted** / *2 requested* / **Pending
approval**, with the same "not held for you yet" sentence. No backend status
semantics were touched.

**Defaults that started in the past (finding 5).** `defaultRecurrenceFor` took
the resource's local *date* and used the first window's opening time, so a
resource open 09:00–17:00 seeded 09:00–10:00 **today** at 15:30 in its own zone
— a first occurrence already elapsed, refused with `BookingInThePast` after a
round trip. It also ignored `maxDurationMinutes` when falling back to an hour,
and could propose a duration shorter than `minDurationMinutes` by clipping to
closing time — both of which failed the form's *own* guards on open. It now
takes the resource-local date **and** time, walks forward eight days (a weekday
only recurs on the eighth) for the first opening span that can hold a legal
booking, starts at the next quarter hour when that span is already under way,
honours both duration bounds, and shrinks to what is left of the span rather
than running past closing. A property test asserts that whatever it proposes
passes `validateRecurrenceForm`, across five schedules × five times of day.

**A submittable form guaranteed to book nothing (finding 6).**
`outsideOpeningHours` deliberately says nothing when a resource publishes no
windows (there is nothing to judge against), so an active resource with an
empty schedule produced a form that validated cleanly and whose every
occurrence was certain to come back `OutsideAvailability`. That is now an
explicit state — `recurrenceUnavailableReason`, with three cases:
`noOpeningHours`, `durationLimitsConflict` (min > max) and `noBookableWindow`
(no window long enough for the shortest allowed booking) — which replaces the
fields with an explanation and disables the submit. **Deliberately not
conflated with "fully booked" or "blacked out"**: those are answers only the
server has, they change by the hour, and a series is *expected* to collect some
of them per occurrence (FR-5.4). The `defaultRecurrenceFor` /
`recurrenceUnavailableReason` pair is covered by a test asserting the two
always agree — a null default always comes with a reason to show, and a null
reason always comes with a default.

**The "Book a recurring series" CTA on the list and detail screens was left
alone**, which finding 6 raised as optional. The list renders `ResourceSummary`,
which carries no `availabilityWindows` at all, so it *cannot* know; suppressing
the CTA only on the detail screen would make the same resource behave
differently depending on which screen it was reached from, and would leave the
member with no explanation of why an action had disappeared. The booking screen
now explains it in place instead.

**Idempotency-key durability: a decision, not a migration (finding 7).** The
pending `{ key, body }` lives in the component, so a reload, a navigation away
and back, or a browser crash takes the retry guarantee with it — a later submit
of the same series mints a new key and could duplicate an attempt that had in
fact committed. Persisting it (sessionStorage, scoped per user and resource)
was considered and **rejected for this pass**: it trades one silent failure for
its mirror image — a key that outlives the attempt it belongs to makes a
*deliberate* second identical series resolve silently to the first — and it
needs a stale-key story (TTL, scope, invalidation) that is real design work,
not a hardening tweak. The honest recovery already exists and is what this app
says everywhere else an outcome is unknown, so the limit is now **stated on
screen** instead ("that holds while this page stays open and the form is
unchanged; if you reload or edit it, check My Bookings instead") and in
`BookingComponent.pendingAttempt`'s own comment. Worth revisiting if a
`GET /recurrence-rules` read ever lands: at that point the right fix is to
*ask* whether the series exists, not to remember a key for longer.

**Verification**: `npx ng test --watch=false` — 27 files, 556 passed, 0 failed;
`npx ng build` clean (the three SCSS budget warnings pre-date this pass).
**Every new regression test was proven against the old code**: the six fixed
behaviours were reverted in place and the suite re-run, failing 34 of the new
tests and no others, before being restored. The browser walkthrough is still
outstanding for the same reason as Phase 3's — no automation is available here
— so what is verified is the rendered DOM in vitest plus the contracts these
screens consume, not a human click-through.


---

## Phase 4 — Calendar, booking detail & cancellation

**Step 1 (types and services) shipped on 2026-09-18**, and later the same day
the owner re-planned the phase around it. Both halves are recorded here because
the sequence is the interesting part: a step delivered against a screen that was
then cancelled, which cost nothing.

### Step 1 — the read and cancel contract

`booking.models.ts` gained `BookingSummary`, `BookingDetail`,
`BookingDetailApproval`, `ApprovalDecision`, the cancel request/response pair,
`ListBookingsParams` and the sort/reason-length constants;
`recurrence.models.ts` gained the series-cancel pair; `BookingsService` gained
`list()`, `getById()` and `cancel()`; `RecurrenceRulesService` gained
`cancel()`. 16 new vitest tests (572 total, 0 failed), build clean.

**Every shape was confirmed against the running API rather than read off the C#
records alone**, following Phase 3 step 1's precedent. Five things that came out
of it:

- **`sort` is a typed union**, `BookingSortField | \`-${BookingSortField}\``,
  mirroring `SortOption.TryParse`'s own grammar — stricter than
  `ListResourcesParams.sort`, which is a bare `string`. Both halves verified:
  `sort=quantity` is a 400 naming the whitelist, `sort=-startsAtUtc` a 200.
- **`userId` and `scope` are deliberately absent** from `ListBookingsParams`.
  Not merely unused surface — a plain member's token sending `scope=tenant`
  answers **400 ValidationFailed**, confirmed live, so shipping the parameter
  would break the screen rather than sit idle. Decision `0002`'s reach is still
  Phase 6's to build.
- **The `!== undefined` guard in `buildListParams` is load-bearing, and the bug
  it prevents was reproduced rather than assumed**: `?status=` (an empty string)
  model-binds to a null enum and answers **200 with every booking** — a silently
  widened query, not an error. A truthiness check would have produced exactly
  that on a cleared filter control.
- **A real `Withdrawn` approval exists in the dev database and reads oddly on
  purpose**: `decision: "Withdrawn"`, `decidedAtUtc` set, `decidedByUserId`
  **null** — there is no decider, only a fact (`ApprovalRequest.Withdraw`).
  `BookingDetailApproval` types the two independently for that reason. It is
  what cancelling a `Pending` booking produces, which is this phase's own step 5.
- **Both cancels are non-idempotent as documented**, proven end to end: a second
  `POST /bookings/{id}/cancel` answers `422 BookingNotCancellable`, a second
  series cancel `422 RecurrenceRuleNotCancellable`. The series cancel returned
  `cancelledBookingIds` with all three occurrences' ids.

Everything created for the probes was cancelled afterwards — one one-off and a
three-occurrence weekly series — so the dev database carries only cancelled rows
from this step. The owner's five live bookings were not touched, re-checked
after cleanup.

### The re-plan, later the same day

**The owner's observation: a My Bookings page makes no sense once a calendar
exists, since the calendar shows bookings anyway.** Checked against the source
PDF rather than taken on instinct, and it holds — the task list names a
*calendar view rendering bookings* and *cancellation and blackout handling in
the UI*. "My Bookings" was this plan's own decomposition, never a mentor
requirement, so merging tightens the fit with CLAUDE.md §12's rule that the
roadmap mirrors the work package rather than an invented build order.

**What the merge deliberately did not drop.** Skipping the phase outright would
have taken four things the calendar does not provide: the booking detail read
(the calendar needs a click target, and FR-5.2 wants each occurrence
independently viewable), cancelling a booking (FR-4.4, and a literal task-list
item), the occurrence-vs-series choice (FR-5.3), and blackout-cancellation
rendering (decision `0019`). All four survive as steps 4–6. What actually went
away was the paged row list and its URL filters, and nothing else.

**Two calls settled in the same conversation**, both written up in
`docs/wp7-plan.md`:

1. **Cancelled and Rejected bookings are not drawn.** Neither holds any time, so
   neither has a cell to occupy, and `NotificationKind.Cancelled`/`Rejected`/
   `SeriesCancelled` already tell the member by email. The contract detail that
   makes this a *rendering* rule rather than a query parameter:
   `ListBookingsQueryRequest.Status` takes one value, not a set, so the client
   cannot ask for "everything except cancelled" — it fetches the bounded window
   and filters. The accepted cost, stated rather than glossed: a cancellation's
   reason, including decision `0019`'s blackout snapshot, becomes readable only
   by direct link to `/bookings/:id`.
2. **The calendar becomes the landing screen** at `/calendar`, with `/home`
   redirecting and the My Bookings nav item deleted. Home had been a placeholder
   since WP-6, and a grep confirmed **no work package or PRD section ever
   assigned it a job** — so nothing was displaced. The one piece of this that is
   a rewrite rather than a repoint is the unknown-outcome message from Phase 3
   step 5 ("your booking *may* have been created — go check"): on a list the
   booking would be at the top, but on a calendar the member has to be told
   which date to look at.

**Phase 5's number is retired rather than reused.** Renumbering would have
falsified every existing reference to "Phase 5, the hard problem" — in this
file, in CLAUDE.md, in the plan's earlier sections and in the commit history —
for nothing but a tidier sequence.

### Step 2 — the calendar shell and the bounded fetch (2026-09-18)

Both designs arrived before the step started (`design/calendar_month_design.png`,
`design/calendar_week_design.png`) — the first phase in this package to begin
with its design in hand rather than receiving one mid-build. 59 new vitest tests
(631 total, 0 failed), production build clean.

`features/calendar/`: `calendar-range.ts` (the URL contract, week/month
boundaries, the UTC fetch window, day-cell layout and the week hour axis) plus
the component. The pure module is kept out of the component for the same reason
`availability-grid.ts` is — none of it is Angular-specific, and the parts most
likely to be wrong are worth testing without a TestBed.

**The wiring was the larger half of the step.** `/calendar` is the landing
route; `/home` and `''` redirect to it; `my-bookings` is deleted; the nav item
is "Calendar", and the `home`/`bookings` icons were removed with their items
rather than left as unreachable branches in the template's switch;
`approverGuard` now bounces to `/calendar` directly rather than through
`/home`'s redirect, so the URL it names is the one the visitor actually lands
on.

**The one rewrite among the three repointed booking-screen links** is the
unknown-outcome message — §7's idempotency gap as the member experiences it. It
now carries `?view=week&date=…` for a one-off and `?view=month&date=…` for a
series. "Check My Bookings" was sufficient when a newly created booking would be
at the top of a list; a calendar has no top, so the link has to name the period
or it is worse than what it replaced.

**A test-infrastructure problem this surfaced, worth remembering.** Making
`/home` a redirect broke `app.routes.spec.ts`, which had used it as the neutral
"some authenticated shell child" — it now lands on the calendar, whose window
fetch was left open, and `httpMock.verify()` failing there **corrupted the
shared TestBed for every spec file that ran afterwards** ("Cannot configure the
test module when the test module has already been instantiated"). The visible
symptom was 23 failures across six unrelated files, with a different subset
failing on each run. Those tests are about guard wiring rather than any
particular screen, so they now navigate between two placeholder routes that
fetch nothing; the two tests that genuinely do land on `/calendar` (the new
redirect tests, and the approver-guard bounce) answer its request explicitly.
This is the same class of cross-file TestBed corruption Phase 2 step 5 hit with
`takeUntilDestroyed`.

**Timezone discipline in the tests, because CI and this machine disagree.** The
calendar reads instants in the *viewer's* zone (wp7-plan.md §3), GitHub Actions
runs in UTC and local development here is CET — so a hard-coded `"...T13:15:00Z"`
literal in an assertion is silently environment-dependent. Every instant in the
new specs is built from local components (`new Date(2026, 8, 24, 9, 0)`), and
the two booking-screen assertions that now check a calendar deep link derive the
expected date the same way rather than hard-coding it.

**Deviations from the designs**, per the owner's instruction to follow them
closely but adapt what disagrees with the app's own conventions: the sidebar
drops "Home" and "My Bookings" (both still in the designs) and adds "Approvals"
for an eligible approver; the breadcrumb is "Calendar" rather than
"Home > Calendar", matching every other screen since WP-6; the week view's hour
axis defaults to the design's 08:00–18:00 but **widens to contain whatever the
week holds**, since a booking outside fixed hours would simply be invisible; a
chip shows the title falling back to the resource name, which is what both
designs depict; and the loading, empty and error states — which neither design
has — were built. The empty state says "Nothing booked in this month" rather
than "you have no bookings", because a window-bounded fetch cannot know about
anything outside it.

**Deliberately left to step 3**, so the grid, the navigation and the fetch could
be reviewed on their own: how a chip actually *looks* — the `Pending`
distinction, the muted past statuses, the recurrence marker, and the "+N more"
overflow affordance the month design shows.

**Verified against the running API**: the exact September-2026 window request
the component builds answered `200` with 8 rows — the owner's 5 live bookings,
which the calendar draws, and 3 `Cancelled` ones, which the status rule drops.
The browser walkthrough remains outstanding for the same reason as every prior
phase: no automation is available in this environment.

#### The week grid's chips sat below their own times (owner, same day)

Found by the owner looking at the screen — the third bug in this package found
that way and not by the suite, after the availability screen's `<select [value]>`
and the silently-clamped `?quantity=16`. **Two independent one-row errors,
compounding:**

1. The grid drew one row per *label* — eleven for an 08:00–18:00 window — while
   `hourSpanStylePercent` computed offsets as a percentage of the ten-hour
   *span*. A chip's `top: 20%` therefore resolved against a column an hour
   taller than the window it was computed from, putting 10:00 at 123px where it
   belonged at 112px, and growing worse down the day.
2. `.hour-line` was a `border-bottom`, so each line sat at its row's *foot*
   while that row's label sat at its *head* — a second full-hour offset in the
   same direction.

**Why no existing test caught it, and why this is the same lesson again.** The
percentage *string* is identical under both readings — `top: 20%` is what the
correct and the broken version both emit — and jsdom performs no layout, so
there were no pixels to measure. The assertion that existed (`style.top` is
`'20%'`) passed throughout and was never wrong; it simply was not about the
thing that broke. What is checkable is the **basis**: that a grid row is an hour
*span*, and that a label and a chip beginning on that hour resolve to the same
offset.

**The fix removes the class of bug rather than the instance.** A new
`minuteOffsetPercent` is the single place a time becomes a vertical position;
`hourOffsetPercent` (labels) and `hourSpanStylePercent` (chips) both go through
it, so they cannot drift apart by construction rather than by two calculations
agreeing. The gutter's labels are absolutely positioned through that function
instead of taking a grid row each, the columns draw one row per hour span
(`hourRows()`, deliberately one shorter than `hourTicks()`), and the lines
became `border-top`. Both ends of the window are still labelled, which is why
there is one more label than there are rows.

**Both regression tests were proven against the old code**: reverting the
template to its pre-fix form fails exactly the two new tests and no others.

#### Two more, from the same screen (owner, same day)

Reported with a screenshot (`design/calendar_bug.png`), and both turned out to
be the *same* mistake seen twice: **the day header and the columns were two
grids in two different boxes, only one of which scrolled.**

1. **The columns did not line up with their own day headers.** A scrollbar is
   laid out *inside* the scrolling box, so `.week-body`'s seven columns were
   each a couple of pixels narrower than `.week-header`'s — a drift that
   accumulated across the week to about 17px by Sunday, which is exactly what
   the screenshot shows.
2. **08:00 could not be brought into view at any scroll position.** The opening
   label is centred on the grid's own top edge (`translateY(-50%)`), so half of
   it sat above `.week-body`'s content box and was clipped by that same
   `overflow: auto`. Scrolling to the top could not reveal it, because it was
   not above the scroll position — it was outside the box.

**The fix removes the split rather than compensating for it.** `.grid` is now
the single scroll container for both views, with the day header `position:
sticky; top: 0` inside it. The two grids are then the same width by
construction, instead of by guessing at a scrollbar width that is neither known
nor constant across platforms. `.week-body` gained a symmetric `padding-top` to
match its existing `padding-bottom`, so both edge-straddling labels have room;
container padding sits outside the grid area, so the row heights the chip
offsets are percentages of are untouched. Every direct child of `.grid` is
`flex: none` — load-bearing, not defensive, since a flex item shrinks to fit by
default and the grids would otherwise compress into the visible height and leave
nothing to scroll. The narrow-screen rules lost their now-redundant
`overflow-x`, and horizontal scrolling improved as a side effect: the header
travels with the columns instead of having to be kept in sync.

**These are testable after all, which was worth checking rather than assuming.**
jsdom performs no layout, so neither symptom can be measured here — but it
*does* resolve the component's stylesheet, confirmed by probing
`getComputedStyle` before writing anything. So the five new tests assert the
mechanism the fix rests on: one scroll box, no second one on the body, a sticky
header, room for the straddling labels, and children that keep their natural
height. All five were proven to fail against the pre-fix stylesheet, and no
others did.

**The standing lesson, now three times over in this package**: for anything the
user sees, the assertion has to be about what actually determines what they see.
`top: 20%` was true and useless; the row *count* was the thing. A passing
percentage said nothing about column widths; the *scroll box* was the thing.

### Step 3 — chips, and proving the volume claim (2026-09-18)

18 new vitest tests (658 total, 0 failed), build clean.

**Status is never carried by colour alone.** `Pending` takes the design's dashed
outline *and* gains "(Pending)" in its own label — the one distinction a member
acts on, since FR-7.1 means the slot is not held yet. `NoShow` gains
"(No-show)", because that is information rather than decoration. `Completed` is
muted but unannotated: the unremarkable past, with nothing to do about it. Each
chip carries an `aria-label` giving the whole thing as one sentence (time,
label, status, series membership, and whether it is a clipped piece of a longer
booking), since the visual chip splits across four elements that read badly
announced separately.

**The overflow affordance expands the day in place — a decision, not a detail.**
The design shows "+2 more" but not what it does, and there is no day view to
send anyone to, so expanding is what makes the capped chips reachable at all.
The expansion is deliberately **not** in the URL: it is a disclosure inside one
cell rather than cross-screen state, which is the distinction the
prefer-the-URL rule actually draws. It clears when the window changes, since the
cells it referred to are gone and a surviving date string would expand an
unrelated day. The summary row takes a chip's *place* rather than sitting below
the full set — otherwise a capped four-booking day would be exactly as tall as
an uncapped one and the cap would buy nothing on the day it matters.

**No cap in the week view**, deliberately: a week chip is positioned by time
rather than stacked, so its DOM is already bounded by what can physically fit in
a day, and hiding one would leave a gap in the grid rather than a shorter list.

**The responsiveness criterion was measured rather than asserted.** Month view,
increasing volume (jsdom, so indicative rather than a browser figure): 50
bookings → 19 ms / 50 chips; 260 (the benchmark wp7-plan.md names) → 21 ms / 56;
500 → 41 ms / 56; 1000 → 64 ms / 56. **The chip count plateaus at 56 while the
data grows twentyfold** — that is the cap working, and it is the property the
suite asserts (35 cells × at most 3 chips) rather than a timing threshold, which
would be flaky in CI and would not say *why*. The residual growth is the single
O(n) pass laying rows into cells. Taken with step 2's bounded fetch, the cost of
rendering a month is flat in how much history the member has.

**Verified against the running API**: a real three-occurrence weekly series was
created, confirmed to come back with `recurrenceRuleId` set on every occurrence
— the only thing the recurrence marker keys off — and cancelled afterwards, so
the dev database is back to the owner's five live bookings. The `Pending` path
needed no fixture: three of those five already are.

#### Two more in the week view (owner, same day)

Reported as "the cards aren't shown fully at the bottom", with a screenshot. The
clipping was real, and the screenshot showed a second problem alongside it that
had not been noticed.

1. **A short booking's chip was shorter than its own content.** A week row is a
   fixed 56px per hour, so a 30-minute booking is 28px — while the chip stacks a
   time line above a label line, about 40px. `.week-chip` is `overflow: hidden`,
   so the booking's *name* was silently swallowed. The `min-height: 28px` that
   was supposed to protect against this was itself too small to matter.
2. **Overlapping bookings were drawn on top of one another.** Every chip had
   `left: 3px; right: 3px`, so three overlapping afternoon bookings occupied the
   identical box and whichever came last in the DOM hid the other two. A member
   with two bookings at the same time is entirely ordinary — different
   resources, or a pooled one — so "only one thing at a time" was never a safe
   assumption to have built in.

**The fixes.** `layOutDay` packs a day's entries into side-by-side columns by
the standard interval-graph sweep: entries are grouped into clusters of
transitively-overlapping bookings, and within a cluster each takes the first
column whose previous occupant has already ended. The column count is the
*cluster's* rather than the day's busiest moment, so a crowded morning does not
squeeze the afternoon's lone booking into a sliver, and a freed column is reused
rather than the day growing a new one per booking. Touching is not overlapping —
back-to-back bookings each keep the full width.
`isCompactChip` gives anything under 45 minutes a one-line layout (time and
label side by side, the label ellipsised) rather than a two-line one it cannot
fit. That threshold is a duration rather than a pixel measurement precisely
because the row height is fixed: 56px per hour means two lines need about 43
minutes' worth of column.

**Verified the way the previous two were**: reverting the template and
stylesheet to their pre-fix form fails three of the four new DOM tests. The
fourth — that a full-hour booking keeps its two-line shape — passes against both,
and is kept as a guard on the threshold rather than a regression test, since a
compact layout applied to *everything* would be the obvious wrong fix.

### Step 4 — the booking detail screen (2026-09-18)

`features/booking/detail/` on `/bookings/:id`, plus calendar chips becoming real
`<a>` elements that point at it. 25 new vitest tests (695 total, 0 failed),
build clean.

**A route rather than a panel.** FR-5.2 asks that each occurrence of a series be
independently viewable, and a booking worth discussing is worth linking to — the
same instinct that put Phase 3's selected slot in the URL. The chips are
anchors rather than click handlers so middle-click, copy-link and open-in-new-tab
all work, matching what the 2026-09-16 accessibility pass did to the resource
card's title.

**It has to render a cancelled booking honestly even though the calendar will
never route anyone to one.** Step 3's status rules mean `Cancelled` and
`Rejected` are not drawn, so the only ways here are a direct link, a bookmark,
or the booking screen's own "check your calendar" message — all of which still
resolve, and all of which are most likely to be used at exactly the moment
someone wants to know what happened. Hence the three cancellation readings
survive: `cancelledByUserId === userId` is the member's own, a different actor
is an administrator (decision `0002` records the actor separately precisely so
this is visible), and a **null** actor beside a real reason is a blackout —
`Booking.CancelForBlackout` leaves it null because there is no person behind it,
and the reason carries decision `0019`'s text snapshot.

**The resource is a second, best-effort fetch whose failure is silent.**
`GetBookingQueryResponse` carries `resourceName` but no `timeZoneId`, so without
it the screen cannot say what the span means on the room's own clock — but every
other fact on the page is still true, so losing it costs one line rather than
the screen. Same reasoning as the availability screen's blackout fetch. It is
guarded on arrival too: the `switchMap` covers the booking fetch only, so a slow
resource read for a previous booking is dropped rather than landing on a newer
one.

**The viewer's zone leads, the opposite emphasis from the booking form one
screen back.** The form led with the resource's zone because decision `0003`
makes that the zone the availability *question* was asked in, and the member
chose against that reading. Reading a booking back is the ordinary calendar case
(wp7-plan.md §3), where what a person wants is when to turn up.

**`spanLabels`/`instantLabel` moved into `local-date.ts` at their second caller**
rather than the usual third, for the same reason `formatDurationWords` moved at
its second: user-visible copy rendering the *same booking's* span on two screens
in one flow, where a drifted second copy would be a visible inconsistency.

**Verified against the running API, every branch against a real row**: a
`Pending` booking carrying `approval.decision: "Pending"` and a real expiry; a
self-cancelled booking where the actor equals the owner; a row exercising three
branches at once — `Cancelled`, a `Withdrawn` approval with a null decider, and
`recurrenceRuleId` set, which is exactly what cancelling a `Pending` occurrence
of a series produces; and `404 BookingNotFound` for a real-but-nonexistent guid.

**One flagged edge, deliberately not handled**: an all-zeros guid answers **400
ValidationFailed** rather than 404, because the validator treats `Guid.Empty` as
a malformed request rather than a lookup that missed — so it lands in the generic
error state with a retry that cannot help. Reachable only by hand-typing that
exact id, so it is recorded rather than given a special case.

### Step 5 — cancelling a booking (2026-09-18)

Confirm-then-act on the detail screen, with an optional reason. 29 new vitest
tests (724 total, 0 failed), build clean.

**A third dialect rather than a second mapper.** `rejection/cancel-rejection.ts`
supplies only the vocabulary — `BookingNotFound`, `BookingNotCancellable`,
`ConcurrencyConflict`, and `ValidationFailed` on the one control a member can
edit. Every message sends them to **look again rather than try again**: a
refusal has just proven the screen's copy of the rule stale, and the create
dialect's way out (re-check availability) has nothing to do with cancelling.

**`BookingRejection.mayHaveBeenCreated` was renamed `outcomeUnknown`.** With a
third dialect setting it, the old name would have meant "may have been
*cancelled*" at one of three call sites. The flag always meant one thing: the
write may have landed, no retry is safe, go and look.

**The no-retry rule is inherited for a different reason than the create path's.**
There a repeat could double a booking (§7's idempotency gap); here it would
overwrite `CancelledByUserId`, `CancelledAtUtc` and the reason with a second
actor's, so the record of who called the meeting off would quietly change. Same
conclusion, and the only action offered on failure is "Reload this booking".

**`canCancel` mirrors `Booking.CanBeCancelled`** — not terminal and
`EndsAtUtc > now`, the second half on the *end* so a meeting under way can still
be called off. "Now" is read once on load rather than ticking: a booking that
ends while the screen sits open still shows the button, the server answers 422,
and the dialect explains it. Better than a button vanishing under the pointer.

**A series occurrence's button says which one it cancels** ("Cancel this
occurrence"), so the ambiguous single button the phase rules out never ships
even as an intermediate state. Step 6 adds the series option beside it.

**The approval is the one thing the cancel response does not carry**, and
leaving it reading "Pending" on a cancelled booking would be a visible lie. The
screen mirrors `ApprovalRequest.Withdraw`, verified live rather than assumed.

**Verified end to end against the running API.** A real `Pending` booking was
created on the approval-gated 3D Printer and cancelled with the exact body the
screen sends. The 200 carries the cancellation trio and the freed interval and
**no `approval`** — which is precisely why the screen mirrors that transition
itself. Re-reading answers `Cancelled` with `approval.decision: "Withdrawn"`,
`decidedAtUtc` set and `decidedByUserId` null, exactly what `applyCancellation`
writes. A second cancel answers `422 BookingNotCancellable`.

**One sentence of the step's own plan does not apply, and is corrected rather
than quietly skipped**: it said the calendar "must drop the chip from the window
it is already holding rather than re-querying", which assumed cancelling happens
*on* the calendar. It happens on the detail screen, and returning to the
calendar is an ordinary navigation that recreates the component and re-fetches
its window — so the chip disappears for free, and no cross-screen state sync was
built because none is needed.

### Step 6 — the series choice (2026-09-18)

FR-5.3's two-way choice on the detail screen: *this occurrence* or *the whole
remaining series*, never one button ambiguous about which it means. 21 new
vitest tests (745 total, 0 failed), build clean.

**The two actions are gated by different rules, and the client can only check
one.** `Booking.CanBeCancelled` is not terminal **and** `EndsAtUtc > now`;
`RecurrenceRule.CanBeCancelled()` is `Status == Active` and **nothing else — no
time component**. A live series therefore stays cancellable from an occurrence
that is itself past or already cancelled, so the two buttons appear
independently rather than one implying the other. That asymmetry is easy to miss
from the API shape alone and was read off the domain entities before anything
was built.

**A contract gap this exposed, handled rather than papered over.**
`GetBookingQueryResponse` carries `recurrenceRuleId` but not the rule's status,
and there is no `GET /recurrence-rules/{id}` to ask — so the client cannot know
whether a series is still Active. The screen offers the action optimistically
and lets `422 RecurrenceRuleNotCancellable` say the series is already cancelled.
Same "server is the authority" trade `canCancel` makes about a stale clock, for
a stronger reason: here there is no way to check at all. Worth a read endpoint
in a future backend package, not a reason to hide a working action.

**Two dialects in one file.** `cancel-rejection.ts` exports both
`describeCancelRejection` and `describeSeriesCancelRejection` — one
member-facing action from one screen, whose copy must stay parallel. Separate
maps rather than one merged one because the codes they *share*
(`ConcurrencyConflict`, `ValidationFailed`) need different words: "this booking"
and "this series" are not interchangeable to the person reading them.

**This booking is only crossed out if the response says it was.** A series
cancel reaches occurrences with `EndsAtUtc > now` and leaves finished ones
alone, so a member on a completed occurrence watches the rest go while this one
stays — correct, and it would read as a bug if the screen crossed it out anyway.

**Verified end to end with a real four-occurrence weekly series.** Cancelling
one occurrence answered 200 and left the series alone. Cancelling the series
then answered 200 with **3 of 4** ids — **the already-cancelled occurrence is
excluded from `cancelledBookingIds`**, so the reported count is genuinely what
this action freed rather than the series' length. That is both what the panel
claims and what the merge depends on, and it was worth proving rather than
assuming. A second series cancel answered `422 RecurrenceRuleNotCancellable`.

### Step 7 — the sweep, and Phase 4 closed (2026-09-18)

9 new vitest tests (754 total, 0 failed), build clean.

**Focus now follows the confirm disclosure in both directions** — the
accessibility item the step names, and the one thing on that screen a mouse user
never notices being wrong. Opening a confirmation moves focus onto the heading
that says *which* cancellation is about to happen; backing out returns it to the
button it came from rather than dropping it on `<body>`; a success moves it to
the outcome that replaced the panel. All three via `afterNextRender`, since the
target does not exist until the template has reacted to the signal.

**The overflow control was the last genuinely unusable thing.** "+2 more" said
neither what it belonged to nor that it was a disclosure, and a month can carry
35 of them. Now `aria-expanded` plus a spelled-out label.

**400px**: the booking detail's label column was the only fixed measure on the
screen, so rows stack rather than wrapping mid-value and the cancel actions go
full width.

**The full flow was walked against the running API** — browse → resource →
availability → book (`201 Confirmed`) → the calendar's bounded window request
(booking drawn) → detail → cancel (200) → the same window again, drawing
**zero** chips. That last step is the direct evidence for step 5's claim that
the chip disappears with no cross-screen state sync.

**The browser click-through remains not done and is not claimed**, in this phase
or any before it — no automation exists here. Five bugs in this package were
found by the owner clicking and none by the suite, so it stays a real gap.

**Phase 4 is closed.** Seven steps, re-planned mid-phase after step 1 shipped.
A full current snapshot of the application now lives in
[`docs/STATE-OF-THE-APP.md`](../STATE-OF-THE-APP.md) — what works, what is
verified, what is deliberately absent, and what is left.

### Frontend restructure — 2026-09-18

Not a step: the owner paused between steps 4 and 5 to reorganise the frontend.
98 files moved, no behaviour changed, 695 tests still passing and the build
clean. The new shape is documented in CLAUDE.md §3 and wp7-plan.md §6;
**paths quoted anywhere above this point in this file predate it.**

Every feature is now split by what a file *is* — `components/<component>/` (one
folder per component, its three files and nothing else), `services/`, `models/`,
helper folders named for what they do (`grid/`, `date/`, `arrival/`,
`rejection/`, `recurrence/`), and `tests/`. The absolute rule is that **no
`.spec.ts` sits outside a `tests/` folder**.

**The moves and the import rewrites were computed, not hand-edited.** A script
built the old→new map, then for every `.ts` file resolved each relative
specifier against its *old* directory, mapped the target through the move map,
and re-relativised it against the *new* one — so a file that moved and a file
that merely imported something that moved were both corrected by the same rule.
Hand-editing ~100 import paths across two passes is exactly the kind of work
that produces one silent mistake, and `git mv` kept the history. The only
verification that matters here is that the build resolves every module and the
whole suite still passes, which both did on the first run of each pass.

Two things worth knowing afterwards:

- **Vitest needed no config change** — it globs `src/**/*.spec.ts`, so spec
  location was never part of the contract. That also means the tests-folder rule
  is a convention this project enforces by review rather than something the
  build will catch.
- **`templateUrl`/`styleUrl` never needed touching**, because each component's
  three files moved together. That is the practical argument for the per-
  component folder beyond tidiness.

Left alone deliberately: `core/` and `shared/` keep their internal shape (they
already group by concern) and gained only tests folders, so
`core/notifications/` is the one place a component still sits beside a service.
And the cross-feature imports this does *not* fix — `local-date.ts` living in
`availability/date/` while booking and calendar both use it, `calendar-range.ts`
imported by the booking screen, `booking-arrival.ts` imported by the
availability screen — are pre-existing and deliberate ("the consumer owns the
contract"), not artefacts of the move.

---

## Phase 6 — Approval queue UI

Planned 2026-09-21, before any code, the same way every phase since WP-3 has
been. The step list and the reasoning behind each step live in
[`docs/wp7-plan.md`](../wp7-plan.md); this file records what actually happened.

### Two calls settled before the phase started (2026-09-21)

**No design is waited for.** The queue is built on the app's existing card
vocabulary, the same route the booking-detail and cancel screens took — both of
which also shipped without one. The outstanding design pass over calendar /
booking detail / cancel now picks the queue up with them rather than the phase
stalling on an asset that has not been drawn.

**`createdAtUtc` is added to the backend list DTO**, overriding wp7-plan.md
§7's rule that a frontend work package does not patch the backend. The gap was
found while checking the contract rather than while building against it:
`ListBookingsQueryResponse` carries no `CreatedAtUtc`, so the queue's
**requested-at** column — "how long has this been waiting", which is the column
that makes a queue a queue — could not be rendered from the list response at
all. The asymmetry is what made it worth raising: `BookingSortFields` **does**
whitelist `createdAtUtc`, so the endpoint would happily *order* by a field it
would not *return*, and a queue sorting oldest-first could sort correctly while
rendering nothing to justify the order.

Three options were put to the owner — omit the column and sort oldest-first,
fetch `GET /bookings/{id}` per visible row, or add the field — and the third was
chosen. The two backend gaps this package has raised have now been answered
differently, which is the point of asking rather than applying a blanket rule:
`POST /bookings`' missing idempotency key went to a future package because it
needs an operation record, a header, a resolution path and a migration; this one
was granted because it is a single field on a projection that already reads the
column for its own sort, and needs no migration at all.

### Step 1 — `createdAtUtc` on the list row (2026-09-21)

Two lines of production code: the field on the record, and `b.CreatedAtUtc` on
the projection. **1066 unit + 508 integration, 0 failed** (integration was 507),
`dotnet build` clean with 0 warnings.

**The step's own plan was wrong about its blast radius, and that is worth
recording rather than quietly correcting.** It predicted the three unit-test
fake builders would need updating. They did not: `BookingFakes`, `ApprovalFakes`
and `RecurrenceRuleFakes` name `ListBookingsQueryResponse` only as a generic
argument and answer with `PagedResult<…>.Empty(…)`, so the record is constructed
in exactly one place in the entire solution — the repository's projection. That
is a property of the codebase worth knowing: a positional DTO here is cheap to
widen precisely because nothing else builds one.

**The `Z` assertion is made twice, deliberately.** The new test
(`List_CarriesTheRequestedAtStampOnEachRow`) asserts the stamp equals the create
response's and that its `Kind` is `Utc`; `Reads_ReturnInstantsWithAUtcDesignator`
was then extended to check the **raw JSON of a list row**, not only of a detail
body. Only the second catches the failure that actually matters. A typed test
client deserializes an instant correctly whether or not the payload carried its
designator, so a test that only round-trips through `HttpClient`'s JSON reader
would stay green while a browser read every queue row's requested-at as local
time — §4.3's stated silent failure, and the same class of mistake as this
package's five owner-found bugs, where the assertion that existed was true but
was not about what the user saw.

### Step 2 — wire types and the approver contract (2026-09-21)

No screen. `booking.models.ts` and `BookingsService` only, the same
contract-first shape Phase 4's step 1 took. **762 vitest tests, 0 failed** (was
754), `npx ng build` clean.

**Every shape was confirmed against the running API, and one of them contradicted
a comment in the codebase.** `BookingsController.List`'s own header still reads
"UserId and Scope are TenantAdmin-only (decision 0002); a plain member sending
either gets 400" — which was true when it was written and has been half wrong
since WP-5 Phase 3 widened `ListBookingsQueryRequestValidator` so an **Approver**
may also ask for `scope=tenant`. The validator is what is actually enforced. The
live probe settled it: the seeded Approver's token gets `200` and two Pending
rows, not a `400`. Worth knowing before the queue is built on the assumption, and
worth knowing generally — this is the second time in two steps that the thing
that was true was the code rather than the prose beside it.

What else the probes established, none of it assumed:

- **A plain member sending `scope=tenant` really is `400 ValidationFailed`**
  with `errors.Scope`. That is why the parameter stays off the calendar's own
  calls rather than being sent harmlessly: it would break the landing screen for
  every member.
- **`userId` is refused for an Approver too**, `400` with `errors.UserId`, which
  is why it stays out of `ListBookingsParams` entirely rather than being carried
  as unused surface.
- **Step 1's field works end to end**: `?sort=createdAtUtc` orders oldest-first
  and the row carries the stamp with its `Z`. The two halves of the requested-at
  column were proven together rather than separately.
- **Both decision responses are exactly the four fields typed** —
  `{id, status, decidedByUserId, decidedAtUtc}` — with `status` coming back
  `Confirmed` from approve and `Rejected` from reject, and a `null` note accepted
  on both.
- **Non-idempotence is real rather than merely documented**: a second approve on
  the same booking answers `422 BookingNotPending`. That is the same answer the
  losing approver gets when two people decide at once, which is what step 6 will
  force deliberately.
- **The note limit is exactly 500 and FluentValidation reports it under `Note`**:
  501 characters is `400` with `errors.Note` naming both numbers, 500 passes
  validation and reaches the handler. Step 5's dialect needs that field name to
  place the message on the right control, so it was read off the wire rather than
  inferred from the C# property.

**Two probe bookings created, one cancelled, one left Rejected.** Both were on
the 3D Printer on 2026-11-16; the approved one was cancelled afterwards, and the
rejected one stays `Rejected` because a terminal booking is not cancellable.
**The owner's two seeded Pending bookings were deliberately not touched** — step
6 and the Phase 6 demo both want them — and the tenant-scoped count was re-read
at 2 afterwards to prove it.

**One stale comment and its matching test were corrected rather than left.**
`ListBookingsParams`' header said "userId and scope are deliberately absent",
true for Phase 4 and now half wrong, and the spec
`never sends scope or userId — the widening belongs to Phase 6` said the same. It
was replaced by **two** tests rather than deleted: `scope` goes out when asked
for, and is still absent when not. The second is the one worth having — the
calendar reads through this same `list()`, and `scope=own` appearing in its URL
would be this client restating a default the server already owns (decision
`0015`), which is the exact shape every other parameter in `buildListParams`
avoids.

### Step 3 — the queue screen (2026-09-21)

`/approvals` is a real screen. **783 vitest tests, 0 failed** (was 762),
`npx ng build` clean, and the exact request the component makes was fired
against the running API before the screen was called done.

**The request is the design decision, and it is one request rather than two.**
`GET /bookings?scope=tenant&status=Pending&sort=createdAtUtc`. An Approver and a
TenantAdmin send byte-identical bytes; the server narrows the rows itself
through `ApprovalReach` — unrestricted for an admin, assigned-resources-only for
an approver (decision `0018`). The component therefore does not branch on role
and there is a test asserting it sends no role-dependent parameter at all. A
client-side branch would be a second copy of an authorization rule sitting beside
the real one, and a copy that *cannot* agree with it: which resources an approver
gates is not in the token. `approverGuard` already keeps the route and its nav
item away from anyone who cannot approve, so "you have no reach here" is not a
state this screen has to render — which matters, because it would be
indistinguishable from an empty queue.

**Oldest first, which is what step 1 was for.** A queue's default order is the
order people have been waiting in, and that is the only ordering the requested-at
column can justify. The endpoint accepted `sort=createdAtUtc` long before it
returned the field; the two halves now work together, and the live probe
confirmed both in one request.

**The formatting is a pure module, not component methods.**
`queue/approval-queue.ts` holds `toQueueRow` and `waitingLabel` with their own
spec, the same split `calendar-range.ts` and `availability-grid.ts` already keep.
Two calls inside it are worth knowing before editing:

- **The span reads in the viewer's zone, not the resource's** — the opposite
  emphasis from the booking form, and the same as the booking detail. Decision
  `0003` governs *availability*, because "Monday 9am" is the reading a member
  chose against when picking a slot. An approver is not choosing a slot; they are
  judging one against their own day. The resource's zone is one click away on
  `/bookings/:id`, which is where an approver who needs it is already going.
- **Requested-at renders twice on purpose**: `Waiting 4 days` beside the absolute
  stamp. A relative label alone cannot be checked against anything; an absolute
  one alone makes the reader do the subtraction. And a stamp *ahead* of the
  browser's clock reads `Just now` rather than negative time — the server's clock
  and the browser's are not the same clock, so a request created seconds ago can
  arrive stamped a moment in the future, and "Waiting -1 minutes" is the kind of
  visible nonsense that makes a reader distrust the rest of the row.

**The tests assert the DOM, per this package's own lesson.** The series badge is
checked as a rendered element present on one card and absent on another, the
detail link as a real `href`, the empty state as its own sentence, the pager as
two buttons whose disabled state and resulting `page` parameter both hold. Five
bugs in WP-7 were found by the owner clicking and none by the suite, and every
one of them would have passed a signal-level assertion.

**Empty got real copy rather than a shrug**, because for this screen empty is the
ordinary case: an approver with nothing waiting is a healthy Tuesday, not a
failure to find anything. "Nothing is waiting for you", plus a line saying when
requests will appear.

#### Raised, not decided: the queue shows requests whose slot has already passed

Both seeded Pending requests are for slots on 2026-09-17 and 2026-09-18, and
today is 2026-09-21. They are still Pending because **nothing expires them** —
the stale-approval-expiry job is specified, its `ExpiresAtUtc` column exists and
`ApprovalDecision.Expired` is in the enum, but no worker runs today
(`STATE-OF-THE-APP.md` §1: "not exercised in anger yet"). So the queue honestly
renders what the API returns, which includes requests it is now too late to act
on usefully.

Three things could be true and the work package settles none of them: a past
request could carry a marker, sort separately, or not appear at all. Deciding
silently would be inventing a requirement (CLAUDE.md §11), so it is raised here
and in the plan. Worth noting the backend does not refuse the decision either —
`dbo.ApproveBooking` re-checks capacity, blackouts and archival, none of which a
past slot violates — so an approver *can* approve a booking that has already
ended. That is a backend question, not a screen one.
