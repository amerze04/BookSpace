---
name: report-back
description: Use proactively at the end of every BookSpace coding task, right before giving the final summary to the user — produces the mandatory end-of-task report in the repo owner's required shape (Summary / Files / Migrations / Verification / Notes). Always invoke this before writing a final response that reports what was done, whenever any file was created, modified, or deleted.
---

# Reporting back on a BookSpace task

At the end of every task — every time control returns to the repo owner —
produce a summary in this exact shape. Concise, but complete: every file
touched gets a line. No exceptions, no "and some minor edits".

```markdown
## Summary
One or two sentences: what was accomplished and whether it is working.

## Files
- `backend/src/BookSpace.Domain/Entities/Booking.cs` — created — booking entity with status enum and rowversion.
  The aggregate every write path in the system ends at, and the only place a
  status transition is expressed as code rather than as a string.
- `backend/src/BookSpace.Infrastructure/Persistence/BookSpaceDbContext.cs` — modified — added Bookings DbSet, global query filter.
  The single unit of work, and one of the three §4.2 isolation mechanisms: the
  filter here is what makes a forgotten `WHERE OrgId` harmless.
- `backend/src/BookSpace.Api/Controllers/BookingsController.cs` — modified — POST endpoint now returns 409 on SlotUnavailable.
  The HTTP surface for booking creation; it only binds and dispatches, so the
  rejection reasons stay in the handler.
- `backend/tests/BookSpace.UnitTests/BookingTests.cs` — modified — covers the new transition
- `docs/decisions/0020-something.md` — created — the decision behind it

## Migrations
- `20260819_AddBookingTables` — creates Bookings, ApprovalRequests; adds IX_Bookings_Resource_Start

## Verification
- `dotnet build` — passed
- `dotnet test` — 24 passed, 0 failed
- (or: not run, and why)

## Notes
- Anything incomplete, assumed, or deferred.
- Any open decision this touched.
```

## Rules for the report

- **Every file touched appears in the list**, with created / modified /
  deleted and a short phrase on what it does or what changed.
- **A file in `Domain`, `Application`, `Infrastructure` or `Api` gets one or
  two further sentences saying what the point of the file is** — the job it
  does in the system and why it exists, not a restatement of its name. The
  short phrase says what changed; these sentences say why the file is there
  at all, so the owner can defend it without reopening it.
  Test files, docs, migrations and frontend files are **listed only** — the
  created/modified phrase is enough for those.
- If more than ~15 files, group them by project (`Domain`, `Application`,
  `Infrastructure`, `Api`, `frontend`) but still list each one.
- Do not paste code back into the summary. The owner can read the files.
- Do not claim something builds or passes unless it was actually run. If it
  was not run, say so under Verification.
- Flag anything left half-done in Notes rather than letting it look finished.
- If you deviated from CLAUDE.md's rules for a reason, say which rule and why.
