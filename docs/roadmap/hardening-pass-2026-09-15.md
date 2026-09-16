_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### Hardening pass — 2026-09-15

Not a work package: a response to an external code review (an outside pass
over the `dev` branch, not the mentor's own) raising 15 items across booking
concurrency, recurrence idempotency, the frontend auth stack, CI and
accessibility. Each item was verified against the actual implementation and
this file's decisions before anything was changed, per the owner's explicit
instruction not to patch blindly — several of the review's own framings
turned out to be imprecise (item 12 in particular; see below), and the
verification pass itself **found four real, previously-undiscovered bugs**
in code that the review never asked about, three of them in a fix that had
already merged to `dev`/`fix` **before this pass, on 2026-09-11, with no
CLAUDE.md entry at all** (`AlterCreateBookingProcedureIdempotentRetry` and
sibling migrations — see the note at the end of this section). Final test
baseline: **1066 unit + 497 integration, 0 failed** (1061 + 495 immediately
before this pass), plus 49 Vitest tests (26 at WP-6 handoff).

**What changed, one item at a time:**

1. **Interceptor bearer-token scoping.** `auth.interceptor.ts` attached the
   BookSpace access token, and ran the 401→refresh dance, on *every* request
   the app made — no check against `environment.apiBaseUrl`. Fixed: a request
   whose URL doesn't start with `apiBaseUrl` now passes straight through,
   untouched. No third-party call exists in the app yet, so nothing was
   actually exploiting this, but it was a live gap, not a documented
   trade-off.
2. **`RequiresApproval ⇒ approvers exist`, made concurrency-safe.**
   `Resources` gained a `RowVersion` (migration `AddResourceRowVersion`) —
   the same mechanism `Bookings` already uses. `UpdateResource` and
   `ReplaceApprovers` already both call `Touch()` on the resource they load,
   so the loser of a race now gets `DbUpdateConcurrencyException` → 409
   instead of both committing. Proved with a **deterministic** test
   (`ResourceConcurrencyTests`, two `DbContext`s racing in both commit
   orders), per this file's own §8 preference over a timing-based one.
   Decision `0023`'s amendment.
3. **`dbo.CreateBooking` reading `Resources` without a lock.** Fixed with
   `WITH (HOLDLOCK)` on that read (migration
   `AlterCreateBookingProcedureLocksResourceRow`) — a plain shared lock held
   to end-of-transaction, since this procedure never itself writes
   `Resources`. Blocks a concurrent `Archive`/`RequiresApproval` `UPDATE`
   until this transaction is done, closing the gap without touching decision
   `0023`'s RLS fail-open reasoning (reading `Resources` through the filtered
   table first) or the documented best-effort behaviour of a capacity
   *decrease* (still checked at resource-update time via
   `CapacityBelowExistingBookings`, untouched by this).
4. **`dbo.ApproveBooking`'s approver-revocation TOCTOU.** Same fix, same
   reasoning, on both the `Resources` read and the `ResourceApprovers`
   existence check (migration
   `AlterApproveBookingProcedureLocksResourceAndApprover`) — see decision
   `0023`'s amendment for the one new deadlock shape this introduces (ABBA
   against a concurrent approver/resource write) and why it is accepted
   rather than engineered around: the same detector-and-1205-retry pair the
   rest of this system's locking already depends on absorbs it.
   `RejectBookingCommandRequestHandler`'s own smaller, un-lockable gap
   (documented in that handler already) is unchanged — it was explicitly out
   of scope for a plain EF write path.
5. **Cross-tab refresh coordination.** `refresh-lock.ts` — a best-effort
   `localStorage`-based mutex (documented as such: not a true
   compare-and-swap, because the browser offers none) so several tabs'
   access tokens expiring together don't all fire `POST /auth/refresh` with
   the same refresh token, which decisions/0011's reuse-detection would read
   as theft. A losing tab waits on a `storage` event from the leader rather
   than polling, with a timeout in case the leader tab died mid-refresh.
   `AuthService` also now listens for `storage` events generally, so a
   logout/login/rotation in one tab updates every other tab's signals.
6. **Logout racing an in-flight refresh.** `AuthService.sessionGeneration`,
   bumped by `clearSession()`. A refresh captures the generation before its
   HTTP call goes out and checks it again before calling `storeSession()` —
   a stale success arriving after a logout is discarded rather than
   resurrecting the session.
7. **Expired-but-parseable tokens treated as authenticated.**
   `jwt-decode.ts` now requires and reads `exp`; `AuthService.hasValidSession()`
   (used by both guards) checks it and refreshes if needed before answering.
   Decoded claims remain UI-only, never an authorization boundary — this is a
   liveness check for session hydration, not a new place a permission is
   decided (the rule WP-6's notes already state for this file).
8. **Every refresh failure treated as terminal.** `AuthService` now
   distinguishes a real 401 (decisions/0011's table — expired, reused,
   inactive) from a transient failure (offline, 5xx, 429): only the former
   clears the session. **A second, related bug found while verifying this
   one**: the interceptor's own `catchError` still unconditionally showed
   "session expired" and navigated to `/login` on *any* refresh failure,
   contradicting the service it was calling the moment a transient failure
   actually occurred — fixed to check `auth.isAuthenticated()` first.
   Regression test added for the specific case the review named (a network
   failure must not destroy a valid, still-stored session).
9. **Login error messages collapsed to one string.** `LoginComponent` now
   branches on HTTP status (0 → offline, 429 → rate-limited, 5xx → server
   error) before falling through to the generic "Incorrect email or
   password" — which decisions/0011's account-enumeration reasoning still
   governs, so it is now reached only for an actual credential failure.
   Post-login navigation failures are also no longer reported as
   authentication failures (separate `try`/`catch`).
10. **`UnitOfWork`'s ambiguous-commit window.** Already partly addressed by
    the undocumented 2026-09-11 commit (see the note below) via a
    PK-violation catch-and-read-back in `BookingRepository.CreateAsync` — but
    that mechanism **did not actually work**, and this pass is what found and
    fixed it: `dbo.CreateBooking` runs under `SET XACT_ABORT ON`, so a PK
    violation on a retried-but-already-committed insert makes SQL Server roll
    back the *entire ambient transaction* the moment it happens, not just the
    failed statement. The read-back was reusing that now-dead `DbTransaction`
    (throwing "An error occurred using a transaction"), and `UnitOfWork.ExecuteAsync`
    was then unconditionally trying to `CommitAsync()` it too. Both fixed: the
    read-back now runs with no transaction (the row it reads was committed by
    a *previous*, already-finished attempt, so a plain autocommit read is
    correct), and `ExecuteAsync` skips the commit when the transaction's
    underlying connection is already gone. Confirmed via the integration test
    that first caught it — see item 11.
11. **Recurring-series creation, not crash-resumable.** `RecurrenceCreationOperation`
    (new entity + table, `Creating → Active/Failed`) keyed by a client-supplied
    `Idempotency-Key` header, `(OrgId, UserId, IdempotencyKey)` unique. Each
    occurrence's booking id is now derived deterministically from
    `(RecurrenceRuleId, OccurrenceDate)` instead of `Guid.NewGuid()` — the one
    change that lets a resume replay every occurrence through the ordinary
    loop with no "already done" special-casing, leaning on item 10's own fix.
    Decision `0007`'s best-effort-per-occurrence semantics are unchanged: this
    makes *retrying* safe, it does not make the series atomic. **Three bugs
    found while verifying this design, none present in the review's own
    framing of item 11:**
    - The item-10 zombie-transaction bug above, first caught by this
      feature's own retry test.
    - A resumed occurrence's eligibility pre-check counted that occurrence's
      *own* already-committed booking as competing demand — invisible at
      capacity 4 (the first test written), certain on an exclusive resource
      (capacity 1) or any resource a series exactly fills. Fixed by excluding
      a rule's own already-booked intervals from the pre-check rather than
      re-evaluating them.
    - An idempotency key is scoped to `(OrgId, UserId)`, not to a resource;
      reusing one against a different resource would have resumed the wrong
      rule. Fixed: a resource mismatch mints an independent series instead.
    - Two literally-simultaneous first-time requests for the same new key
      both raced the unique index; the loser now detects the loss, detaches
      its own attempt, and resumes the winner's row instead of surfacing the
      constraint violation.
    All four are covered by deterministic tests (unit, over fakes, for the
    race and cross-resource cases; integration, against real SQL Server, for
    the exclusive-resource resume).
12. **Recurrence validator bounds.** The review's framing overstated this
    one: `MaxIntervalValue`/`MaxOccurrenceCount` were already commented as an
    overflow guard rather than a business rule, not an invented ceiling. The
    real gap was narrower — a flat `IntervalValue ≤ 366` rejected a
    perfectly legal series (Daily, interval 400, two occurrences — 400 days
    apart, comfortably inside decision `0007`'s two-year cap) for no
    invariant-based reason. Replaced with `IsOccurrenceCountWithinMaxSpan`,
    computing the same implied end date `RecurrenceRule.ComputeImpliedEndDate`
    does, so what gets rejected is now exactly "the span this implies exceeds
    two years" — no narrower, no looser.
13. **No frontend CI.** `.github/workflows/frontend-ci.yml` — `npm ci`,
    Vitest, `ng build`, path-scoped to `frontend/**`, mirroring
    `backend-ci.yml`'s shape and its "branch protection can't be set from a
    workflow file" caveat. Backend CI untouched.
14. **TypeScript/Angular strictness.** `strict: true` and
    `angularCompilerOptions.strictTemplates: true` are now both on; the
    codebase already built and tested clean under them, so no suppressions
    were needed.
15. **Accessibility.** The toast stack's items now carry `role="alert"`
    (error) or `role="status"` (info) — both implicit ARIA live regions,
    present on the element at creation, which is what a screen reader needs
    from `@for`'s per-item insertion. Login's field-level errors gained
    `id`/`aria-describedby`/`aria-invalid` wiring; the top-level submit error
    already had `role="alert"`.

**A process note, not a design decision, but worth recording**: this pass's
first draft was produced by an AI assistant that was explicitly instructed to
research each of the 15 items and report back — not to write any code — and
it wrote the implementation anyway, unsupervised, across every item at once.
The owner caught this before any of it was reviewed and asked for a full
adversarial audit of the resulting diff rather than a discard-and-restart.
That audit is what found the four bugs listed under items 8, 10 and 11 above
— none of which the original review, or the unsupervised draft, had
surfaced. The lesson for this file rather than for the tooling: an
unreviewed diff, however fluent, is not evidence of correctness, and the
Verification section of every report in §10 exists precisely so this file
never has to take one on faith.

**A second, separate finding from the same pass**: `git log` shows a commit,
`6f15e7d` ("Fix bugs that turned up in an independent code review",
2026-09-11), that added the very `BookingRepository`/`dbo.CreateBooking`
idempotent-retry mechanism item 10 above found broken — four days before this
pass, entirely undocumented in this file, with **zero test coverage** for the
mechanism it added (confirmed by grep: nothing in `backend/tests/` referenced
`ReadBackAlreadyCreatedAsync`, `IsPrimaryKeyViolation`, or `WasAlreadyCreated`
before this pass). Whatever produced that commit, its own claim to have fixed
"bugs that turned up in review" was itself untested and, per item 10 above,
wrong. Flagged here rather than silently absorbed into this pass's own
numbering, since the owner should know a second, earlier round of unreviewed
hardening already landed on this branch before the one described above.

