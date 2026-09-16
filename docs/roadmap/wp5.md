_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-5 — Recurrence, Approvals & Time Correctness — **Done** (2026-09-09)
Source doc: `docs/Work Packages - Week 4.pdf` (week 4, backend track).
**Plan: [`docs/wp5-plan.md`](docs/wp5-plan.md)**, approved 2026-09-08 after all
seven of its shape questions were put to the owner in one sitting (the same
process WP-3 and WP-4 each went through). The one genuinely open decision it
owned — the DST fall-back policy for a recurring occurrence, §9's last open
item — is now [`0024`](docs/decisions/0024-dst-fallback-recurrence-policy.md):
an ambiguous local time resolves to the **earlier** of its two candidate UTC
instants, for both an occurrence's start and its end.

**Phase 1 ("creating a series") is done**, in two chunks: **1a, the pure
`RecurrenceExpansion`** (`RecurrenceRule.OccurrenceDate(int)`,
`IResourceTimeZone.IsInvalidLocalTime`, and a same-day `LocalEndTime >
LocalStartTime` constructor guard that `RecurrenceRule` was missing entirely
before this); **1b, the write path — done 2026-09-09**:
`POST /recurrence-rules` on `TenantMember`, calling `dbo.CreateBooking` once
per occurrence inside its own `IUnitOfWork` (never one transaction for the
whole series, per `0007`), reporting every occurrence as created, skipped
(DST), or refused (FR-5.4) — and a new `AppException.Extensions` mechanism so
the all-refused 422 (`NoOccurrencesCreated`) can carry the same breakdown a
success would have. See wp5-plan.md §9 for what 1b found while building it,
including a real staging-order bug (an approval/notification pair for a
declined occurrence lingering into the next occurrence's save) and — raised
by the owner after reviewing this chunk, fixed the same day — a
compensating-delete fix so an all-refused series leaves **no** trace at all:
neither an orphaned `RecurrenceRule` row nor a stray spring-forward-skip
notification for an occurrence from a series the client was told reserved
nothing.

**Phase 2 ("occurrence view/cancel and whole-series cancel") is done,
2026-09-09.** Per-occurrence view/cancel fell out of Phase 1 for free — an
occurrence *is* a `Booking` with `RecurrenceRuleId` set, so `GET /bookings/{id}`
and `POST /bookings/{id}/cancel` already worked; this phase closed the one gap
(`RecurrenceRuleId` added to `ListBookingsQueryResponse`) and built
`POST /recurrence-rules/{id}/cancel`: cancels the rule and every occurrence
still holding a live claim (decision `0002`'s `EndsAtUtc > now` window,
reapplied per occurrence), one summary notification rather than one per
occurrence (`NotificationKind.SeriesCancelled`), through plain EF — no
`IUnitOfWork`, on the same reasoning that already keeps the single-booking
cancel and `BlackoutCascade` out of `dbo.CreateBooking`'s territory.

**Found and fixed while building it**: `RecurrenceRules` had no tenant
isolation at all — no `OrgId`, no query filter, no RLS — a WP-1 gap that
Phase 1's create path never exposed (it only ever creates a rule, scoped
implicitly through its resource) but Phase 2's cancel-by-id endpoint would
have, immediately: a `TenantAdmin`'s dropped owner filter had nothing under it
restricting it to their own tenant. Fixed as `0025`, applying decision `0014`'s
exact pattern. `0026` is the smaller, related schema change Phase 2 needed
regardless: widening `CK_Notifications_HasContext` so a `SeriesCancelled`
notification can anchor to a `RecurrenceRuleId` alone, with no single
occurrence date to hang it on.

**Phase 3 ("approvals") is done, 2026-09-09.** `dbo.ApproveBooking` inherits
`0023`'s four-part lock design whole, over the Pending row's own status guard;
`Booking.Reject`/`CanBeRejected`; `POST /bookings/{id}/approve` and
`.../reject` on `TenantMember`, reachable by a TenantAdmin over any Pending
booking in their tenant or by an assigned `Approver` (`ApprovalReach`, decision
0018) — never both an owner and a resource restriction at once, since the two
roles widen along different axes. Proved at both levels before the endpoint
was written, mirroring WP-4's `dbo.CreateBooking` split:
`ApproveBookingProcedureTests` (14 tests, including two decisions racing the
same booking and an approval racing a concurrent cancel) at the procedure
level, `BookingApprovalEndpointTests` (18 tests) through the real pipeline.
`GET /bookings?scope=tenant` is now also valid for an `Approver`, restricted to
the resources they are assigned to approve
(`BookingOwnerFilter.AnyOwnerRestrictedToResources`) rather than to a member —
the queue widens by resource, `userId` stays TenantAdmin-only. `ListBookingsQueryResponse`
and `GetBookingQueryResponse` both gained the booker's `UserName` (denormalized
the same way `ResourceName` already was), and `GetBookingQueryResponse` gained
an `Approval` section (`GetBookingApprovalDetail`) carrying the request's
outcome, not just that one exists — closing WP-4's loose ends 3 and 4.

Found while building it: the retry-safety hazard Phase 1 found for a
declined-and-retried occurrence reappears here in a new shape — a 1205 retry
re-entering `IUnitOfWork`'s delegate can find an `ApprovalRequest` already
decided in memory from the aborted attempt, so `ApproveBookingCommandRequestHandler`
guards `Decide()` with `if (approvalRequest.Decision == ApprovalDecision.Pending)`
before calling it, proved directly by a dedicated unit test.

Test baseline: **1039 unit + 468 integration tests pass, 0 failed** (879 + 406
at WP-4 handoff; 978 + 430 at Phase 2).

- [x] Create recurring bookings (daily/weekly/monthly) with interval and end
      condition. FR-5.1. **Done 2026-09-09** (Phase 1):
      `POST /recurrence-rules` on `TenantMember`.
- [x] Make each occurrence independently viewable and cancellable. FR-5.2.
      **Done 2026-09-09** (Phase 2) — free from Phase 1's `RecurrenceRuleId`
      anchoring, plus `RecurrenceRuleId` added to `ListBookingsQueryResponse`
      (previously detail-only).
- [x] Support cancelling one occurrence or the whole remaining series. FR-5.3.
      **Done 2026-09-09** (Phase 2): `POST /recurrence-rules/{id}/cancel`.
- [x] Surface collisions/blackout conflicts at creation — never drop them
      silently. FR-5.4. **Done 2026-09-09** (Phase 1): every occurrence is
      reported created, skipped (DST), or refused, with its reason code; an
      all-refused series is a 422 (`NoOccurrencesCreated`) carrying the same
      breakdown, never a 201 with an empty list.
- [x] Implement the approval workflow: Pending → approve/reject → notify;
      re-check availability at approval time. FR-7.1–FR-7.5. **Done
      2026-09-09** (Phase 3).
- [x] Store all times as UTC; render in the correct local zone. FR-6.1.
      **Already true by construction** (§4.3's standing rule, in force since
      WP-1) — nothing WP-5 built is a second time-handling path: recurrence
      expands local wall-clock time to UTC once, in `RecurrenceExpansion`, and
      every stored instant is `datetime2(0)` UTC like every other table.
      Checked off here rather than left blank because Phase 4's AC sweep
      confirmed it holds for the tables this package added, not because
      anything new had to be built.
- [x] Define and implement DST-transition behavior for recurring bookings.
      FR-6.2. **Done in Phase 1** (2026-09-09): spring-forward skips the
      occurrence (`0008`), fall-back resolves to the earlier of the two
      candidate instants for both ends (`0024`), both against real
      `America/New_York` tzdata in `RecurrenceExpansionTests`.

Acceptance criteria:
- [x] A recurring series is created, and single occurrences and the whole series
      can each be cancelled. **Met 2026-09-09** (Phases 1–2).
- [x] Conflicting occurrences are surfaced at creation. **Met 2026-09-09**
      (Phase 1).
- [x] Approval re-checks availability, so approving a since-taken slot fails
      safely. AC-5. **Met 2026-09-09** (Phase 3):
      `ApproveBookingProcedureTests` proves the capacity re-check under the
      same lock `dbo.CreateBooking` uses, and the reason codes
      (`SlotUnavailable`, `CapacityExceeded`, `BlackoutPeriod`,
      `ResourceArchived`) reuse WP-3/WP-4's existing exceptions unchanged.
- [x] The DST edge case resolves per the documented policy with no crash or
      silent duplicate. AC-3. **Met** — proved at the layer this codebase
      always proves DST correctness (WP-3's D3/`0021` set the precedent):
      pure-function tests against **real** `America/New_York` tzdata, not a
      fixed-offset fake, in `RecurrenceExpansionTests`
      (`Expand_SkipsAnOccurrenceWhoseLocalStartFallsInTheSpringForwardGap`,
      its end-only sibling, `Expand_OtherOccurrencesInTheSameSeriesAreUnaffectedByOneSkippedDate`
      — the "no duplicate" half, since a skip is a `RecurrenceOccurrenceOutcome`
      with no `Booking` behind it rather than two occurrences landing on one
      instant — and `Expand_ResolvesAnAmbiguousOccurrenceUsingTheEarlierInstantForBothEnds`).
      The write path's own mechanics (a skip enqueues its own notification and
      creates no booking; a series that skips every occurrence still reports
      each one, per FR-5.4) are covered separately, with a fake outcome, in
      `CreateRecurrenceSeriesCommandRequestHandlerTests`. No HTTP-level DST
      test exists deliberately — `CreateRecurrenceSeriesEndpointTests` keeps
      its resources in UTC, the same choice `CreateBookingEndpointTests`
      already made, so the write-path proof and the DST-correctness proof
      don't have to agree with each other to pass.

Notes: its two hard problems and both open decisions are **already settled** —
materialization horizon [`0007`](docs/decisions/0007-recurrence-materialization-horizon.md),
spring-forward policy [`0008`](docs/decisions/0008-dst-spring-forward-policy.md),
blackout vs. series [`0001`](docs/decisions/0001-blackout-vs-recurring-series.md),
availability timezone [`0003`](docs/decisions/0003-availability-timezone.md).
Two more it inherits from WP-4: [`0002`](docs/decisions/0002-tenant-admin-cancellation.md)'s
amendment fixes the four cancellation mechanics FR-5.3 has to reapply per
occurrence, and [`0023`](docs/decisions/0023-booking-concurrency-strategy.md) is
**inherited whole** — `dbo.ApproveBooking` needs the same capacity check under
the same locks over the same index for FR-7.5/AC-5, so it is not a second
strategy to invent.
**Phase 4 ("AC sweep and documentation") is done, 2026-09-09, and closes
WP-5.** No new production code — per the plan, this phase confirms rather than
builds: all four acceptance criteria checked above, each against the specific
tests that prove it rather than by assertion; decision
[`0023`](docs/decisions/0023-booking-concurrency-strategy.md) extended a
second time with `dbo.ApproveBooking`'s own measured concurrency evidence
(no new decision record, per the plan — `0023` already says the procedure
inherits the strategy whole, so this phase adds evidence to it exactly as
WP-4 Phase 3 did); and this section's own tick-off. Final test baseline:
**1039 unit + 468 integration tests pass, 0 failed** — unchanged from Phase
3's handoff, since Phase 4 added no new application code, only the
temporary, reverted procedure weakening that produced `0023`'s figures.

§9's DST **fall-back** case, the one item this package owned that was still
open at handoff, was answered by `0024` in Phase 1 — nothing was left open by
the time this phase started.

**All four Week-4 acceptance criteria are met, all seven FR/task items are
checked, and every decision this package touched is written up.** WP-5 is
complete.

