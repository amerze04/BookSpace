_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-1 — Data Model & Database — **Done** (2026-08-21)
- [x] Model core entities: Tenant, User, Resource, AvailabilityWindow,
      BlackoutPeriod, Booking, RecurrenceRule, ApprovalRequest.
- [x] Define relationships, keys, integrity constraints.
- [x] Decide how tenancy is represented on every ownable entity.
- [x] Plan indexing for availability lookups and overlap checks.
- [x] Produce an ERD; write initial migrations + seed data.

Acceptance criteria:
- [x] ERD exists, presented before any application code — presented to the
      mentor as one of the first steps; the full schema was built on it.
- [x] Migrations run cleanly and seed a realistic multi-tenant dataset —
      `InitialCreate` applied to a real SQL Server instance; seed produces 2
      orgs, 9 users, 4 resources, 20 availability windows, 2 blackout periods,
      2 recurrence rules; re-running is a no-op (idempotent).
- [x] Model represents a two-year weekly recurring booking without redesign —
      cap raised from one year to two (Decision #7 addendum); seed data
      includes a recurrence rule running exactly to the new boundary.
- [x] Every ownable entity is unambiguously tied to a tenant.

Notes:
- The design side of WP-1 (`docs/bookspace-schema-v2.sql`, decisions 0001–0008)
  was already done before this build order started, ERD included; this entry
  tracked turning it into EF Core code, which is now complete.
- ERD: confirmed settled — presented to the mentor early, and
  `docs/bookspace-schema-v2.sql` was built directly on it. `docs/Amer-ERD-
  Feedback.docx` / `-Response.docx` are the review that followed. No separate
  ERD image/file lives in this repo; the schema doc is its record.
- Seed data stopped short of `Bookings`, `ApprovalRequests`, `Notifications`,
  and `RefreshTokens` — the first three because §4.1 requires Booking writes to
  go through `dbo.CreateBooking`/`dbo.ApproveBooking`, which didn't exist yet;
  the last because refresh tokens are issued at login, not meaningful as static
  data. **Updated 2026-09-08 (WP-4 Phase 3)**: the seed now writes three
  bookings per tenant *through the procedure* — two `Confirmed` on Conference
  Room A and one `Pending` on the 3D Printer with its `ApprovalRequest` row —
  anchored to the next weekday at 10:00/11:00/14:00 **local** so the dataset
  never becomes historical, and skipping the seeded Christmas blackout, which
  the procedure would otherwise refuse. `Notifications` and `RefreshTokens`
  are still deliberately empty: nothing dispatches notifications yet (§7), so
  seeded rows would be permanently unsent mail.
- `ResourceApprovers`' forced EF owned-collection cascade (vs. the schema's
  `NoAction`) is a known, accepted, documented deviation — see the comment in
  `ResourceConfiguration.cs`.
