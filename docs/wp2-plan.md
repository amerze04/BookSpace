# WP-2 — Backend Skeleton, Auth & Tenancy: proposed approach

Status: **proposal, not a decision** — drafted 2026-08-21 for the mentor
design review before WP-2 starts. Once discussed, settled points should move
into `docs/decisions/` as numbered decision records the same way 0001–0008
did for WP-1, and this file's checklist should replace the WP-2 entry in the
root `CLAUDE.md` §12.

Source: `docs/Work Packages - Week 1 and 2.docx`, WP-2 section.

---

## What WP-2 asks for

Four work streams, grouped as the source doc groups them:

1. **Architecture & auth** — layered wiring, finish wiring EF Core, login
   issuing access + rotating refresh tokens, refresh rotation + reuse
   detection, RBAC (SysAdmin/TenantAdmin/Approver/Member), and structural
   tenant isolation. The doc calls tenant isolation out as a **hard
   problem**: it must be enforced at a layer that can't be bypassed by
   forgetting a `WHERE` clause — a filter copy-pasted into forty methods is
   explicitly the wrong answer.
2. **Structured logging (Serilog)** — replace the default logger, structured
   properties not interpolated strings, a correlation ID flowed through every
   log line for a request (and later, background jobs), sensible sinks/levels,
   never log secrets.
3. **Global exception handling** — no unhandled exception ever leaks a stack
   trace; domain/validation errors map to consistent `ProblemDetails` with the
   correlation ID attached; the exception is logged once at the boundary, not
   scattered try/catch.
4. **Hand-written mediator** — no MediatR. A dispatcher mapping request →
   handler, with a pipeline for cross-cutting behaviors (logging, validation).
   Also called a **hard problem**: the point isn't reimplementing a library,
   it's making separation of concerns real — controllers stay ignorant of the
   work, every line has to be explainable.

Full acceptance criteria are in the source doc; the short version: login/
refresh/rotation/reuse-detection work, passwords are hashed and never logged,
tenant A can never read tenant B's data even with a forged identifier, every
endpoint enforces role- and tenant-scoped authorization server-side, every
log line for a request shares a correlation ID, unhandled exceptions return a
clean problem response, and controllers dispatch through the mediator with no
business logic living in them.

---

## Proposed sequencing

The four streams aren't equally ready to start — two have no open design
questions and no dependencies on anything else; two have real decisions to
make and depend on each other. Proposed order:

### Phase 1 — cross-cutting plumbing (no design ambiguity)
1. **Finish wiring EF Core.** ~90% done from WP-1 (`DbContext`, configurations,
   migrations, DI registration already exist). What's left: `EnableRetryOnFailure`
   with 1205 (deadlock victim) added, and using
   `Database.CreateExecutionStrategy().ExecuteAsync(...)` for anything
   transactional, per `CLAUDE.md` §5.
2. **Serilog + correlation ID.** Independent of auth and everything else.
   Doing this first means every later feature gets structured logs and a
   correlation ID for free, instead of being retrofitted.
3. **Global exception handling.** Pairs with #2 — the `ProblemDetails`
   response needs the same correlation ID the logging middleware just
   established. ASP.NET Core's `IExceptionHandler` is the natural fit.

### Phase 2 — the mediator, before any real feature exists
4. **Hand-written mediator + pipeline.** Build this *before* login, not after.
   Writing login directly in a controller now and refactoring into the
   mediator pattern later risks the AC it's meant to prevent ("no business
   logic lives in a controller") — temporary code tends to become permanent.
   Prove the mediator with a trivial no-op handler through the pipeline
   first; let login be the first real handler written against it.

### Phase 3 — auth, now with somewhere real to put it
5. **Login + tokens + RBAC.** The schema already fully supports rotation and
   reuse detection — `RefreshTokens.FamilyId` / `ReplacedByTokenId` from WP-1
   are exactly what this needs — so this is mostly application-layer work,
   not data-model work.

### Phase 4 — tenant isolation, last because it depends on auth existing
6. **Structural tenant isolation.** `CLAUDE.md` §4.2 already prescribes the
   three-mechanism design (global query filters, `SaveChangesAsync` `OrgId`
   stamping, RLS via a connection interceptor calling
   `sp_set_session_context`) — unlike auth, this isn't an open design
   question, just an implementation one. It needs a "who is the current
   tenant" accessor resolved from the authenticated principal's claims, which
   doesn't exist until Phase 3 is done. Also flagged as a hard problem in the
   source doc, for the same underlying reason as the mediator: it has to be
   structural, not sprinkled, so it can't be forgotten.

---

## Decisions settled so far

- **Password hashing:** ASP.NET Core's built-in `PasswordHasher<T>` (PBKDF2).
  No new dependency, satisfies "modern algorithm" from the acceptance
  criteria.
- **Pipeline validation:** FluentValidation, per the mentor's direction —
  wired into the mediator's pipeline as a behavior rather than validated
  ad hoc inside handlers.

## Still open

- **JWT claims shape** — not yet decided. Needs at minimum: what identifies
  the user (user id), what identifies the tenant (org id — required for the
  tenant-isolation accessor in Phase 4 to have anything to read), and how
  roles are represented for RBAC checks. Also open: where the signing key
  lives in configuration (never committed, per `CLAUDE.md` §4.4).
