# WP-8 plan — Background Jobs, Notifications & Integrations

Source doc: `docs/Work Packages - Week 7 and 8.pdf` (WP-8 section). Track:
Backend. Mentor-issued, follows the user-management package and the two
hardening passes.

The owner has no background with this topic and asked me to make the calls
myself rather than walk through each one — "I'll be learning as we go." Every
decision below is final for this package; where the reasoning matters for a
later package or a mentor question, it's written out so it can be defended,
same as every other decision in this codebase.

---

## 1. What already exists, and what WP-8 actually adds

Read against the PDF cold, WP-8 looks like it starts from nothing. It
doesn't — most of the schema-level groundwork was laid in WP-1 and the
user-management package, specifically so these jobs could be dropped in
later without a redesign. What's actually missing is narrower than the task
list suggests:

- **No-show is fully decided** (`decisions/0004`). `Booking.IsNoShow(now,
  graceMinutes)` and `Booking.MarkNoShow()` already exist on the domain
  entity; `Organizations.NoShowGraceMinutes` is a real, seeded column;
  `IX_Bookings_NoShowSweep` is the filtered index the task list asks for.
  WP-8's own "OPEN DECISION" about what a no-show is has already been
  answered — nothing to redecide, just cite `0004` and build the job.
- **Stale-approval is mostly decided.** `ApprovalRequest.Expire()` already
  exists; `Organizations.ApprovalExpiryHours` and `IX_ApprovalRequests_Pending`
  already exist. `ApprovalRequests.Decision`'s CHECK constraint has
  `'Expired'` and nothing resembling `'Escalated'` — the schema already
  committed to **expire, not escalate** before this package existed. The one
  real gap: `Booking.Reject(actorUserId, nowUtc)` requires a non-null actor,
  unlike `MarkNoShow`, which already has the null-actor pattern for a
  system-initiated transition. Needs a small domain addition (Phase 4).
- **Reminders are configurable already** (`Organizations.ReminderLeadMinutes`,
  `IX_Notifications_Due`, `Notifications.Attempts`/`LastError`), but **nothing
  today ever inserts a `Reminder`-kind row.** `Confirmed`/`Rejected`/
  `Cancelled`/`ApprovalRequested`/`SeriesCancelled`/`RecurrenceOccurrenceSkipped`
  rows are all written at the right event already (WP-4/5) — they've been
  piling up in the table, unsent, since WP-4, because nothing has ever sent
  anything. The dispatch job (Phase 2) clears that backlog for free; the new
  work is reminder *scheduling*, not sending.
- **Check-in has no endpoint.** `Booking.CheckIn()` exists on the domain
  entity but nothing in `BookSpace.Api` calls it. Without it, every
  `Confirmed` booking is unconditionally a no-show once grace elapses —
  nobody can ever prevent it. Not on WP-8's task list, but it has to be in
  scope, the same way user management flagged gaps the PRD didn't name
  outright (CLAUDE.md §11).
- **`IEmailSender`/`SmtpEmailSender`/`DevelopmentSinkEmailSender`** already
  exist (user-management phase 1). "Integrate a transactional email
  provider" is reuse, not new integration — the job layer adds the
  retry/backoff `SendAsync` deliberately doesn't have (its contract is
  "never throws, returns a result"; retrying belongs to the caller, and
  until now nothing called it more than once).
- **Nothing like a job lease table exists.** Multi-instance locking is
  genuinely new (Phase 1).
- **No ICS anywhere.** Fully new (Phase 5).

---

## 2. Decisions made for this package

The owner delegated these. Each will get written up properly as a numbered
decision doc in Phase 6 (`docs/decisions/0032` onward — `0031` is the current
last); this section is the working reasoning.

**D1 — Job ownership lock: a lease table, not `sp_getapplock`.**
WP-8 frames this as a hard problem and explicitly allows `sp_getapplock` as
an acceptable floor. Chosen instead: a `JobLeases` table (`JobName` PK,
`OwnerId`, `AcquiredAtUtc`, `LeaseExpiresAtUtc`, `LastHeartbeatAtUtc`),
acquired/renewed by a single atomic `UPDATE ... WITH (UPDLOCK, HOLDLOCK) ...
WHERE JobName = @j AND (OwnerId = @o OR LeaseExpiresAtUtc <= @now)` — same
compare-and-swap shape as booking concurrency, scaled down to one row.
`sp_getapplock` ties the lock to one open connection for the run's entire
duration, which sits awkwardly next to "resolve a fresh DI scope per run"
(a scope can span several short-lived connections) and gives no queryable
owner id or renewable heartbeat — an operator can't look at the table and
see who holds what, or how stale a heartbeat is. A lease row is also just
data: it survives a restart, is visible in a query, and needs no server-side
object beyond a table. Deliberately **not** a stored procedure — CLAUDE.md
§4.1 reserves that heavier tool for the one problem only a procedure can
solve (aggregating a range of rows under a range lock); a lease is a single
row, and `UPDLOCK, HOLDLOCK` on it via a plain parameterised `UPDATE` gives
the same guarantee with less ceremony.

**D2 — No EF entity for `JobLeases`.**
Every other table in this schema has a `Domain` entity because something,
somewhere, reads it back through LINQ. Nothing will ever read `JobLeases`
that way — the repository only ever runs one atomic `UPDATE` and checks
rows-affected. That puts it in the same bucket as stored procedures and RLS
policies (§5: "created via `migrationBuilder.Sql(...)`... EF will not
scaffold them"), not the bucket every business entity is in. The table is
hand-written into a migration, `IJobLeaseRepository` talks to it over raw
`ExecuteSqlInterpolatedAsync`, and there is no `DbSet<JobLease>`.

**D3 — Stale-approval expiry gets its own notification kind,
`ApprovalExpired`, rather than reusing `Rejected`.**
A human saying no and nobody looking in time are different facts, and this
codebase's whole reason-code philosophy (§6) is not to conflate distinct
causes behind one label. One more `CK_Notifications_Kind` value, alongside
the `Booking` transitioning to `Rejected` via a new system-initiated method
(see D-domain below) — the *booking's* status only has room for `Rejected`,
but the *notification* explaining why can and should be precise.

**D3a — `Booking` gets `ExpireApproval(DateTime nowUtc)`.**
Mirrors `MarkNoShow`'s null-actor pattern rather than reusing
`Reject(Guid actorUserId, ...)`, which requires a real actor. Guarded by the
same `CanBeRejected()` predicate `Reject` already uses (`Status ==
Pending`) — the transition is identical, only the actor and the calling
context (a job, not a person) differ.

**D4 — A reminder whose computed `SendAtUtc` is already in the past gets
sent once, immediately, rather than skipped.**
A short-notice booking (confirmed less than `ReminderLeadMinutes` before its
start) would otherwise silently never remind anyone. Scheduling clamps
`SendAtUtc` to `max(StartsAtUtc − ReminderLeadMinutes, nowUtc)` — the row is
still created, and the dispatch job picks it up on its very next tick.

**D5 — Check-in: the booking's own owner only, no time-window restriction
beyond `Status == Confirmed`, idempotent on repeat.**
No FR covers this. Restricting to the owner matches every other
member-facing write in this app (cancel, series-cancel); there's no FR
asking for front-desk-style check-in on someone else's behalf, and adding it
would be inventing scope. No earliest/latest bound because "I arrived and
want to check in immediately" is the normal case and the domain method's
only existing guard is the status — narrowing it further would be a rule
nobody asked for (the same trap the WP-7 click-through found the booking
detail screen in: an invented rule with no FR or decision behind it). A
second check-in is a no-op rather than an error or a silently-rewritten
timestamp, matching the deactivate/reactivate convention already
established in user management ("the target state already holding returns
it and writes nothing").

**D6 — ICS as a subscribable, tokenised per-user feed link, not a
JWT-protected endpoint or a per-booking download.**
A calendar app polling a subscribed feed URL cannot attach a bearer token,
so the existing auth model doesn't fit this endpoint at all — this needs a
new, narrow credential. A `CalendarFeedTokens` table (one live row per user,
SHA-256 hash stored like every other bearer secret in this app — decision
`0011`'s shape, reused a third time after refresh and activation tokens),
regenerable via an authenticated endpoint that invalidates the previous
link. The feed endpoint itself (`GET /calendar-feed/{token}.ics`) is
anonymous and looks the token up by hash, the same shape `/auth/activate`
already uses. Content is the user's own `Pending` + `Confirmed` bookings,
with no artificial future cap needed — series materialization is already
capped at two years (decision `0007`).

**D7 — No external ICS library.**
The subset of the format needed (`VCALENDAR`/`VEVENT` with `UID`, `DTSTART`,
`DTEND`, `SUMMARY`, `STATUS`, `DTSTAMP`, `SEQUENCE`, all in UTC `Z` form) is
small and mechanical. A hand-rolled writer avoids a dependency for a single
narrow use, consistent with this project's build-vs-buy calls elsewhere
(MailKit was justified specifically because *no* provider existed yet; here
the reasoning runs the other way — the format is simple enough that a
library buys little).

**D8 — Multi-instance safety is proven by a forced-interleaving integration
test, not a live two-process deployment.**
Same technique and the same justification as
`UserLastAdminGuardConcurrencyTests`: a real two-instance deployment can't
be relied on to interleave by luck inside a test run, so the test forces it
(one runner holds the lease deliberately while a second starts mid-run and
is asserted to wait or take over). If the owner wants to see two real
processes fight over one lease live at some point, that's a demo, not a
test — the test has to force the race either way.

**D9 — `TenantBypassScope` is widened for job repositories in Phase 2, not
Phase 1.**
Phase 1's lease table has no `OrgId`, no query filter, and no RLS policy —
it is genuinely global infrastructure state, not tenant data, so acquiring a
lease needs no bypass at all. The reminder dispatch job (Phase 2) is the
first thing that actually has to scan `Notifications`/`Bookings` across
every tenant in one tick, which is what forces `TenantBypassScope`'s
"only `AuthenticationUserRepository` may call `Enter()`" comment to become
false. That change belongs with the job that needs it.

---

## 3. Phases

Each phase is independently reviewable and buildable, following how every
other package here has been delivered (admin console, user management).

### Phase 1 — Hosted-service + lease foundation, no real jobs yet
- `JobLeases` table (hand-written migration, raw SQL — D2), seeded with one
  row per known job name (`ReminderDispatch`, `NoShowRelease`,
  `StaleApprovalExpiry`) with an already-expired lease, so the first real
  acquire attempt in each later phase succeeds immediately without a
  separate insert-or-update branch.
- `IJobLeaseRepository` / `JobLeaseRepository` — `TryAcquireOrRenewAsync`,
  `ReleaseAsync` (graceful release on clean shutdown, so a stopped instance
  doesn't make its peers wait out the full lease).
- `PeriodicJobRunner` — an abstract `BackgroundService` base, one copy
  shared by all three jobs: fresh DI scope per run, per-run correlation id
  pushed onto the Serilog `LogContext`, honours `CancellationToken` for
  graceful shutdown, catches and logs any unhandled exception from a run
  without killing the host, logs a structured run summary (picked up /
  succeeded / failed / elapsed). Exposes a public `RunOnceAsync` a test can
  call directly, the same way this codebase already tests locking by
  talking to the repository/unit-of-work layer directly rather than waiting
  on real wall-clock timers (`UserLastAdminGuardConcurrencyTests`'s own
  justification, reused).
- `IJobLeaseRepository` registered in `AddInfrastructure`. No concrete job,
  and nothing registered as an actual hosted service in `Program.cs` yet —
  there is nothing to run until Phase 2.
- **Tests** (integration, real SQL Server): forced-interleaving lease
  acquisition (two attempts on the same job name, exactly one succeeds);
  renewal by the same owner; a crashed owner's lease is taken over once it
  expires; a fake job's unhandled exception is caught and doesn't stop the
  next tick; cancellation stops `RunOnceAsync`/the hosted service cleanly.

### Phase 2 — Reminder dispatch job (FR-9.2) + the notification backbone
- Schedule a `Reminder` notification at booking-confirm time (one-off
  confirm, approval confirm, and per-occurrence for a series), `SendAtUtc =
  max(StartsAtUtc − Organizations.ReminderLeadMinutes, nowUtc)` (D4).
- Widen `TenantBypassScope` to name job repositories alongside
  `AuthenticationUserRepository` (D9), with the header comment rewritten
  honestly.
- `NotificationDispatchJob : PeriodicJobRunner` — claims `IX_Notifications_Due`
  rows via `UPDLOCK, READPAST` in bounded batches (configurable batch size
  and poll interval), resolves recipient/subject/body per `Kind` (branching
  for `RecurrenceOccurrenceSkipped`'s dual anchor per decision `0008`, and
  every other kind's `BookingId` anchor), sends via `IEmailSender`, updates
  `SentAtUtc`/`Attempts`/`LastError`, retries transient failures with
  backoff up to a cap, then stops.
- This one job also finally sends every notification kind that's been
  silently queued since WP-4/5.

### Phase 3 — No-show release (FR-9.1) + check-in endpoint
- `POST /bookings/{id}/check-in` — owner-only, calls `Booking.CheckIn()`,
  idempotent on repeat (D5), new `BookingNotCheckable` (409) reason code for
  a booking not currently `Confirmed`.
- `NoShowReleaseJob` — sweeps `IX_Bookings_NoShowSweep`, evaluates
  `IsNoShow(now, org.NoShowGraceMinutes)` per booking's own org (`Bookings`
  already denormalizes `OrgId`, decision `0006`), calls `MarkNoShow()`,
  writes a `NoShowReleased` notification.

### Phase 4 — Stale-approval expiry (FR-9.3)
- `Booking.ExpireApproval(DateTime nowUtc)` (D3a).
- `NotificationKind.ApprovalExpired` added, `CK_Notifications_Kind` widened
  (D3).
- `StaleApprovalExpiryJob` — sweeps `IX_ApprovalRequests_Pending` joined to
  `Bookings` where `ExpiresAtUtc <= now`, calls `ApprovalRequest.Expire()` +
  `Booking.ExpireApproval()`, notifies the booking's owner with the new
  kind. Confirms during build whether `ApprovalRequest.ExpiresAtUtc` is
  already populated at creation time from `Organizations.ApprovalExpiryHours`
  — if not, that wiring is part of this phase too.

### Phase 5 — ICS feed
- `CalendarFeedTokens` (`UserId` unique, `TokenHash`, `CreatedAtUtc`) — D6.
- `POST /users/me/calendar-feed-token` (authenticated) — creates/regenerates
  the caller's token, returns the plaintext link once, never stored or
  returned again (same "shown once" precedent as the invitation link).
- `GET /calendar-feed/{token}.ics` (anonymous) — looks up the token by hash,
  renders that user's `Pending`/`Confirmed` bookings as `VEVENT`s (D7).
  Unknown/revoked token answers a plain 404, outside the app's structured
  error contract, the same way activation failures are deliberately
  uninformative.

### Phase 6 — Multi-instance proof, idempotency proof, verification, docs
- Forced-interleaving lease test at the full job level (not just the
  repository), covering all three real jobs (D8).
- Run-twice / restart-mid-batch tests proving the unique constraint — not
  trust — is what prevents a duplicate send, for all three jobs.
- Update CLAUDE.md §7 and §12 (WP-8 marked done), `STATE-OF-THE-APP.md`.
- Write decisions `0032` (job lease strategy, D1/D2), `0033` (stale-approval
  → `ApprovalExpired` + system-initiated rejection, D3/D3a), `0034` (ICS
  feed token model, D6/D7) — numbers confirmed against the log's actual
  state at write time.

---

## 4. Sequencing note

WP-9 (AI-Assisted Booking) is next after this, per the mentor's numbering,
and per CLAUDE.md §11's standing rule: its tasks don't start before WP-8's
acceptance criteria are met. It gets its own plan when this one closes.
