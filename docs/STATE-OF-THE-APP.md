# BookSpace — state of the app

**As of 2026-09-22**, at the close of **WP-7** — the whole package, not just a phase.

A snapshot of what exists, what is verified, and what is deliberately not built
yet. Written to be read on its own — if you are picking this up cold, or
demoing it, start here. The reasoning behind any individual decision lives in
`docs/decisions/`, the per-package narrative in `docs/roadmap/`.

---

## 1. What works today

### Backend — complete through WP-5

Every feature the first five work packages asked for is built, tested and
running.

| Area | State |
|---|---|
| Multi-tenant data model | 8 core entities, code-first migrations, seeded with 2 orgs / 9 users / 4 resources |
| Tenant isolation | All three mechanisms (query filters, `SaveChanges` validation, SQL Server RLS) over six tables |
| Auth | Credential login, 15-minute access tokens, rotating refresh tokens with reuse detection, four RBAC roles |
| Resources & availability | Full CRUD, availability windows, blackout periods, approver assignment, the bookable-slot query |
| Booking engine | One-off creation through `dbo.CreateBooking` under `UPDLOCK, HOLDLOCK` — **zero double-bookings under concurrent load, the project's primary acceptance bar, proven by test** |
| Recurrence | Series creation fully materialized up front, per-occurrence outcome reporting, DST spring-forward and fall-back policies |
| Approvals | Request/approve/reject with a capacity re-check at approval time (AC-5) |
| Error contract | One `AppException` hierarchy → `ErrorKind` → status, with a machine-readable reason code per failure |

**Not exercised in anger yet:** the three background jobs (reminder dispatch,
no-show release, stale approval expiry) are specified and their idempotency
constraint exists in the schema, but nothing writes `Completed` or `NoShow`
today.

### Frontend — WP-6 and WP-7 both complete

| Screen | Route | State |
|---|---|---|
| Login | `/login` | Done (WP-6) |
| Calendar — **the landing screen** | `/calendar` | Done. Month and week views, bounded fetch, status-aware chips |
| Resource list | `/resources` | Done. Server-side search, type and approval filters, real pagination |
| Resource detail | `/resources/:id` | Done |
| Availability | `/resources/:id/availability` | Done. Custom hour grid, drag-to-narrow selection, blackout labelling |
| Booking form | `/resources/:id/book` | Done. One-off and recurring, full reason-code coverage |
| Booking detail | `/bookings/:id` | Done. Cancel one occurrence or a whole series |
| Approvals | `/approvals` | Done. Tenant-scoped pending queue, oldest first; approve/reject with a note, also on the booking screen |
| Settings, Help | `/settings`, `/help` | **Placeholder** — never scoped |

A member can, today, sign in → browse resources → check availability → pick a
slot → book it (one-off or recurring) → see it on their calendar → open it →
cancel it, or cancel the whole series.

---

## 2. Verification baseline

| Suite | Count | Notes |
|---|---|---|
| Backend unit | 1073 | |
| Backend integration | 511 | Needs a real SQL Server — the in-memory provider has no locking and no RLS |
| Frontend (vitest) | 861 | |

Production build clean. The five named acceptance-criteria tests all pass:
concurrency (AC-1), isolation (AC-4), DST (AC-3), approval re-check (AC-5),
job idempotence (AC-6).

**Every WP-7 screen's request/response contract has been checked against the
running API**, not only mocked — including a full end-to-end pass on
2026-09-18: browse → resource → availability → book → calendar window →
booking detail → cancel → calendar window again, confirming the cancelled
booking is no longer drawn.

### The standing verification gap, and how WP-7 closed it

**No browser click-through can be performed by automation here**, in any phase,
because no browser-automation tool exists in this environment. What automation
verifies is every request/response pair plus rendering assertions in vitest —
not the rendered flow.

**WP-7 closed the gap the only way available: a person walked it.**
[`docs/wp7-clickthrough.md`](wp7-clickthrough.md) is a written script — the
member's path, the approver's path, and ten deliberate wrong turns — which the
owner walked on 2026-09-22. That walk is what met the first acceptance criterion;
the API-level evidence, which had existed since 2026-09-18, was never enough on
its own. **Re-walk the script after any change to the booking or approval
flows.**

This is not a formality. **Eight bugs in WP-7 were found by clicking and none by
the suite**: the availability screen's `<select [value]>` showing the wrong time;
a hand-edited `?quantity=16` silently booking one unit; three separate calendar
layout faults (chips below their stated times, columns out of line with their day
headers, short chips clipping their labels); the booking detail screen's
member-voiced copy shown to an approver; the approval queue's link into a 404;
and an approver blocked from approving their own request.

The pattern in the first seven is the same — the assertion that existed was true
but was not about what determined what the user saw. **The eighth is worse and
worth remembering separately**: the suite was *asserting the invented rule*, so
it was green precisely because it was wrong. A green suite proves the code
matches the tests; that is worth nothing when the test is the invention.

Treat a visual pass as required before signing off any screen.

---

## 3. WP-7 acceptance criteria — all four met (2026-09-22)

| Criterion | State |
|---|---|
| A member completes browse → book → confirm entirely through the UI | **Met 2026-09-22** — walked end to end by the owner through the UI. API-level evidence had existed since 2026-09-18 and was deliberately not treated as sufficient: the criterion asks for a member completing it *through the UI* |
| Recurring bookings render correctly in the calendar | **Met** — occurrences carry a recurrence marker; verified against a real series |
| The calendar stays responsive under realistic data volume | **Met structurally.** DOM is bounded by the chip cap, not by the data: 50 → 1000 bookings holds at 56 chips (19ms → 64ms in jsdom). A browser-level measurement has not been taken |
| An approver can action pending requests from the UI | **Met** — approve and reject from the queue or the booking; the concurrent-decision race forced live (one 200, one 422 "already decided") |

---

## 4. What is deliberately not built

Each of these is a decision with a reason, not an oversight.

- **Resource administration UI** — create, edit, archive, manage availability
  windows / approvers / blackouts. The backend has supported all of it since
  WP-3 and the provided designs assume it, but WP-7's task list is member-facing
  only. Buttons are absent rather than shown disabled. **Now planned** —
  [`docs/admin-plan.md`](admin-plan.md), 2026-09-22.
- **A "My Bookings" list** — cancelled on 2026-09-18. The calendar answers the
  same question, and the source work package never asked for the screen.
- **Cancelled and rejected bookings on the calendar** — neither holds any time,
  so neither has a cell to occupy. The member is told by email
  (`NotificationKind`). The cost, accepted by the owner: a cancellation's
  *reason* is readable only by direct link to `/bookings/:id`.
- **A calendar library** — the grid is custom, so there is one style to reason
  about and nothing fighting the bounded-fetch strategy.
- **httpOnly cookie storage for refresh tokens** — permanently decided against
  (decision `0011`); the XSS-theft risk is accepted rather than doing the
  `AllowCredentials` + CSRF migration.

---

## 5. Known gaps, with owners

**Needs a backend change — belongs to whichever package follows WP-7:**

1. **`POST /bookings` has no idempotency key.** A one-off booking whose response
   is lost cannot be safely retried. The client mitigates as far as it can: it
   never auto-retries, and a transport failure says the booking *may* exist and
   deep-links the calendar to the date. `POST /recurrence-rules` already has the
   key; mirroring it here replaces the mitigation with a real retry.
2. **No `GET /recurrence-rules/{id}`.** No response carries a rule's status, so
   the booking detail screen cannot know whether a series is still active — it
   offers "cancel the whole series" optimistically and lets the 422 explain.
   A read endpoint would let it hide the action instead.
3. **Self-approval is permitted, deliberately.** An approver may decide on their
   own pending request for a resource they gate — from the queue and, since
   2026-09-22, from the booking screen too. The backend has always allowed it
   (`ApprovalReach.ForResources` does not exclude the caller; verified live,
   `200 Confirmed`), and an approver booking equipment they are responsible for
   is ordinary rather than a loophole.

   The booking screen used to refuse, on an invented rule that no FR or decision
   record ever asked for; the owner found it walking the Phase 7 click-through
   and it was removed. Listed here because it is a **policy worth knowing**
   rather than an open question: if self-approval should ever require a second
   approver, that is a backend rule (`ApprovalReach`), not a UI one.

**Frontend, small and open:**

4. **The booking form's `?mode` is read but not written back**, so sharing a URL
   mid-form always shares the one-off view.
5. **`/bookings/00000000-0000-0000-0000-000000000000`** answers 400, not 404, so
   it lands in the generic error state with a retry that cannot help. Only
   reachable by hand-typing that exact id.
6. **A design pass is outstanding** for the calendar, booking detail, the
   cancel confirmation, and the approval queue with its decision panel — the
   owner has flagged this. The calendar was built to
   two provided designs; the others had none and follow the
   app's existing card vocabulary.

**Housekeeping:**

7. `core/notifications/` is the one place a component still sits beside a
   service rather than in a `components/` folder.
8. `calendar-range.ts` does five distinct jobs (URL contract, week/month
   boundaries, fetch window, day layout, hour axis) and is a candidate for
   splitting.
9. `local-date.ts` lives in `features/availability/date/` but is used by
   booking and calendar too. Consistent with the project's "the consumer owns
   the contract" convention, but `shared/` may be the better home.

---

## 6. What's next

**WP-7 is closed and no mentor work package is in flight.** What follows is the
backlog as it stands, in the order it is worth picking up.

1. **The admin console** — tenant admin CRUD for resources, availability
   windows, approvers and blackout periods. **Planned 2026-09-22, not started**:
   [`docs/admin-plan.md`](admin-plan.md), seven phases. Owner-initiated rather
   than mentor-issued, and the largest thing still missing from the application —
   flagged as a gap since WP-7 Phase 1 (§4). Carries the one backend addition the
   plan could not avoid: **`GET /users`**, without which approvers cannot be
   assigned from a UI at all, because nothing in the API lists users.
2. **The design pass** the owner has flagged — the calendar, the booking detail,
   the cancel confirmation, and the approval queue with its decision panel were
   all built without a provided design, to the app's existing card vocabulary.
   The admin console will add five more screens in the same position, so it is
   worth deciding whether that console waits for designs or follows suit.
3. **A backend package to close the two API gaps WP-7 raised and could not fix**
   (§5): an idempotency key on `POST /bookings`, mirroring the one
   `POST /recurrence-rules` already has, and a `GET /recurrence-rules/{id}` so
   the booking screen can stop offering a series cancel optimistically.
4. **The three background jobs** — reminder dispatch, no-show release, stale
   approval expiry. Specified, with their idempotency constraint in the schema,
   but nothing runs them, which is why the approval queue can show requests whose
   slot has already passed.
5. **The small open items** in §5 — the booking form's `?mode` write-back and the
   all-zero-GUID 400.

---

## 7. Conventions that are not obvious from the code

Worth knowing before changing anything; each is stated in full in CLAUDE.md.

- **Booking writes go through `dbo.CreateBooking` / `dbo.ApproveBooking`**,
  never LINQ. It is the only place the no-double-booking guarantee exists.
- **Tenant isolation is structural**, not a `WHERE` clause — three mechanisms,
  all required.
- **Time is UTC in the database; recurrence expands in the application layer.**
  A stored `TimeZoneId` is an IANA id and only an IANA id.
- **Nothing is deleted** — deactivated or archived.
- **Every failure is a named `sealed` subclass of `AppException`** fixing its
  own kind and reason code.
- **The frontend files by what a file *is***: `components/<component>/`,
  `services/`, `models/`, purpose-named helper folders, and **no `.spec.ts`
  outside a `tests/` folder**.
- **For anything a user sees, assert against the rendered DOM** — and prove a
  regression test fails against the old code before keeping it.
