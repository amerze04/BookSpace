# Why this repo restarted (2026-08-20)

## What happened
The first attempt (kept at `BookSpace_initial/` until it's deleted, then only
in this file) jumped straight into Domain entities, EF Core configurations,
migrations, and the concurrency-safe stored procedures — skipping WP-0
(project setup & foundations) and WP-2's cross-cutting foundations (logging,
global exception handling, the custom mediator). That meant real, working
code existed (a passing AC-1 concurrency test, applied migrations against a
real SQL Server) without the basic project scaffolding — README, solution
layout convention, `.gitignore`/`.editorconfig`, a documented local dev
setup — that WP-0 exists to lock down first.

Rather than retrofit foundations under code that had already run ahead,
we're restarting the implementation from WP-0, in order, this time. The
*design* work from the first attempt is not being redone — it's carried
forward as-is (see below) — only the *build order* changes.

## What carries forward unchanged
Everything in `docs/` other than this file is inherited from the first
attempt and remains authoritative:

- `BookSpace_PRD_v1.docx` — the requirements. Unchanged.
- `bookspace-schema-v2.sql` — the database schema, source of truth for the
  data model. Unchanged.
- `decisions/0001`–`0008` — all eight architectural decisions (blackout
  priority, tenant-admin cancellation, availability timezone, no-show
  definition, capacity semantics, `OrgId` denormalization, recurrence
  materialization horizon, DST spring-forward policy). These were resolved
  through real design work and a mentor ERD review; none of that reasoning
  is being revisited.
- `Amer-ERD-Feedback.docx` / `Amer-ERD-Feedback-Response.docx` — the review
  that prompted decisions 0006–0008. Kept for provenance.
- `Work Packages - Week 1 and 2.docx` — the WP-0/WP-1/WP-2 task list this
  restart is now following in order.

## What does not carry forward
- `bookspace-schema-v2-vp-import.sql` — the reduced, constraint-stripped
  variant made only for Visual Paradigm's reverse-DDL parser. It was already
  marked deprecated in the old `CLAUDE.md` and caused the mentor's "fix
  before you build" feedback to flag several already-solved problems
  (`OrgId` redundancy, `UserRoles.Role` typing, `BookingStatus` naming) that
  the full schema had actually handled all along. Not worth carrying into a
  clean repo — the full schema is the only diagram source from now on.
- All previously written code (`BookSpace.Domain` entities, EF Core
  configurations, migrations, `dbo.CreateBooking`/`dbo.ApproveBooking`, the
  AC-1 integration test). The design those implement is still correct and
  should be re-implemented against it — but the code itself was written
  before WP-0's foundations existed underneath it, so it's being rebuilt
  rather than moved over wholesale.

## Where WP-1 (data model) actually stands
Design-wise, WP-1 is effectively already done — the schema, ERD reasoning,
and all eight decisions exist and are documented. What's left of WP-1 is
re-doing the EF Core side (entities, configurations, migrations, seed data)
*after* WP-0's foundations are in place, so it's built in the right order
this time. See the root `CLAUDE.md` for the up-to-date build roadmap.
