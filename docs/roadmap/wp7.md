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
- [ ] Booking form for one-off and recurring bookings, with clear validation
      feedback.
- [ ] Calendar view rendering bookings, including recurring series, without
      choking on volume.
- [ ] Approval queue UI for approvers.
- [ ] Cancellation and blackout handling in the UI.
- [ ] Wire the full flow end-to-end against the real API.

**Hard problem** (not yet reached): the calendar (Phase 5) must stay
responsive with hundreds of bookings and expanded recurring series.

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
