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
- [ ] Availability view for a resource and date range.
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
- The type-icon SVG mapping and type-label strings are duplicated between
  the list and detail components rather than extracted — only the second
  occurrence so far (`BrandMarkComponent`'s own precedent extracts on the
  third). A likely third user is the booking form, Phase 3.

