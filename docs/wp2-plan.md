# WP-2 — Backend Skeleton, Auth & Tenancy: proposed approach

Status: drafted 2026-08-21 as a **proposal** for the mentor design review
before WP-2 started; the sequencing below was accepted and is now being
executed. Settled points should still move into `docs/decisions/` as numbered
decision records the same way 0001–0008 did for WP-1.

**Where the build stands (2026-08-26): Phases 1, 2 and 3 are complete. Phase 4
(structural tenant isolation) is next.** Per-phase status is marked inline
below; the root `CLAUDE.md` §12 checklist carries the detail on what each
completed item actually built.

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

### Phase 1 — cross-cutting plumbing (no design ambiguity) — **done**
1. **Finish wiring EF Core.** ~90% done from WP-1 (`DbContext`, configurations,
   migrations, DI registration already exist). What's left: `EnableRetryOnFailure`
   with 1205 (deadlock victim) added, and using
   `Database.CreateExecutionStrategy().ExecuteAsync(...)` for anything
   transactional, per `CLAUDE.md` §5.
2. **Serilog + correlation ID.** Independent of auth and everything else.
   Doing this first means every later feature gets structured logs and a
   correlation ID for free, instead of being retrofitted.
3. **Global exception handling — done.** Pairs with #2 — the `ProblemDetails`
   response needs the same correlation ID the logging middleware just
   established. ASP.NET Core's `IExceptionHandler` is the natural fit.
   Built as a safety net only: any unhandled exception → 500 `ProblemDetails`
   with the correlation ID and a `reasonCode` extension, no stack trace
   leaked; `DbUpdateConcurrencyException` → 409 (`CLAUDE.md` §5). No
   `AppException` hierarchy was added, and FluentValidation had no caller at
   the time. The `FluentValidation.ValidationException` → 400 mapping was
   picked up in Phase 2 as planned and is now done (with a per-field `errors`
   extension). **Still deferred:** map booking rejections to their
   `CLAUDE.md` §6 reason codes (`SlotUnavailable`, `CapacityExceeded`, ...)
   once that write path exists — the remaining extension point is marked with
   a comment in `GlobalExceptionHandler.Map(...)`.

### Phase 2 — the mediator, before any real feature exists — **done**
4. **Hand-written mediator + pipeline.** Built *before* login, deliberately:
   writing login directly in a controller and refactoring into the mediator
   later risks the AC it's meant to prevent ("no business logic lives in a
   controller") — temporary code tends to become permanent.

   What exists now, all in `BookSpace.Application` (detail in `CLAUDE.md`
   §12): `Messaging/` holds the contracts (`IRequest<TResponse>`,
   `IRequestHandler`, `IPipelineBehavior` + `RequestHandlerDelegate`, the
   public `ISender`, a `Unit` void-substitute) and `Dispatcher`, which
   resolves the closed-generic handler and behaviors for a request's runtime
   type through one reflective bridge call and composes the behavior chain in
   reverse registration order. `Messaging/Behaviors/` holds `LoggingBehavior`
   and `ValidationBehavior`. `AddApplication()` in
   `Application/DependencyInjection.cs` scans the assembly once for
   `IRequestHandler<,>` and `IValidator<>` implementations, then registers the
   behaviors — Logging first so it ends up outermost, Validation second — and
   `ISender`, all `Scoped`.

   **Two things a future session needs to know:**
   - The proof-of-concept slice (`Application/Features/Ping/` +
     `Api/Controllers/PingController.cs`) is **temporary and must be deleted
     as part of Phase 3**, in the same change that makes login the first real
     handler. It's an unauthenticated endpoint that does nothing but exercise
     the pipeline.
   - A validator is only discovered if it lives in the `BookSpace.Application`
     assembly and is typed against the **exact** concrete request type
     (`AbstractValidator<TheCommand>`). A validator in another project, or
     typed against a base class/interface, is silently never found — generics
     are invariant, so `IValidator<BaseCommand>` never satisfies
     `IValidator<TheCommand>`. There is no startup check that a command has a
     validator; a missing one is a silent pass-through.

   Verified by unit tests in `BookSpace.UnitTests/Messaging/` (dispatch,
   behavior ordering, short-circuiting, both behaviors) and by a manual HTTP
   round trip: valid `POST /ping` → 200 with handler logs present; empty
   message → 400 `ProblemDetails` carrying `reasonCode: "ValidationFailed"`
   and per-field `errors`, with the handler's log line absent (proving
   validation short-circuited before it) and the correlation ID on every line.

### Phase 3 — auth, now with somewhere real to put it — **done**
5. **Login + tokens + RBAC.** As predicted, almost entirely application-layer
   work: `RefreshTokens.FamilyId` / `ReplacedByTokenId` from WP-1 were exactly
   what rotation and reuse detection needed, and the only data-model change was
   the email-uniqueness one below. Login is now the first real handler on the
   Phase 2 mediator, and the `Ping` slice is deleted.

   Built: `Application/Abstractions/` (the seven interfaces the handlers depend
   on — password hasher, refresh-token factory, access-token service, clock, and
   the two auth repositories), `Application/Features/Authentication/`
   (`Login`, `Refresh`, `Logout`, plus the shared `TokenIssuer`,
   `AuthenticationResult`, `AuthenticationException`, and reason codes),
   `Infrastructure/Security/` (`JwtOptions`, `JwtAccessTokenService`,
   `PasswordHasherAdapter`, `RefreshTokenFactory`, `SystemClock`,
   `BookSpaceClaims`), `Infrastructure/Persistence/Repositories/`,
   `Api/Authorization/AuthorizationPolicies.cs`, and
   `Api/Controllers/AuthController.cs`.

   **Four decisions came out of this phase** and are recorded properly:
   `0009` (JWT claims + lifetimes), `0010` (global email uniqueness — the
   owner's call, and a real schema gap rather than an unmade decision),
   `0011` (refresh-token hashing, rotation, reuse detection), `0012` (RBAC
   enforcement model).

   **What a future session should know:**
   - **`Jwt:SigningKey` must be set or the app will not start.** Deliberate —
     `ValidateOnStart()` on `JwtOptions`, minimum 32 bytes. user-secrets in
     dev, `Jwt__SigningKey` elsewhere. See the README.
   - Seed data now stores real PBKDF2 hashes of one shared dev password
     (`SeedData.SeedPassword`), so `SeedAsync` takes an `IPasswordHasher`.
   - `RefreshToken.RevokedAtUtc` is an EF **concurrency token**. It looks like
     an odd choice until you need it: it is what stops two concurrent refreshes
     of the same token from both minting a replacement. Do not "clean it up".
   - The only unfiltered `Users` reads in the codebase are in
     `AuthenticationUserRepository`, and that is the named
     `IgnoreQueryFilters()` exception `CLAUDE.md` §4.2 allows. Phase 4 must not
     add more.
   - **Migrations don't apply automatically on startup.** A fresh or reset
     database needs `dotnet ef database update` (see README §3) before
     `dotnet run` — `SeedAsync` will fail on tables that don't exist yet
     otherwise. This predates Phase 3 but only got documented now, prompted by
     resetting the dev database to clear WP-1's placeholder password hashes.
   - **A `CREATE DATABASE BookSpace` file-collision on 2026-08-26 was not a
     leftover from that drop** — it turned out `BookSpace_initial`, the
     pre-WP-0-restart database `RESTART_NOTES.md` describes, had been renamed
     via `ALTER DATABASE ... MODIFY NAME` rather than dropped at the time of
     the restart, so it was still quietly sitting on the default
     `BookSpace.mdf`/`BookSpace_log.ldf` filenames under a different logical
     name. Confirmed empty (schema + 2 migration rows, zero data) and dropped
     — **resolved**, not just worked around; see the README's Database section
     for the general diagnostic (`sys.master_files`, not just `sys.databases`)
     if a similar collision shows up again.
   - **On Windows PowerShell, test the API with `Invoke-RestMethod`, not
     `curl`/`curl.exe`.** Confirmed live: PowerShell mangles the inner quotes
     of a JSON body when it reconstructs the argument list for a *native*
     executable like `curl.exe`, so a body that's correct in the terminal
     arrives at the API malformed. `Invoke-RestMethod` is a cmdlet, not a
     native binary, so `-Body` passes through untouched. Examples in the
     README's "Signing in" section.
   - **Manually verified against a running instance (2026-08-26):** login
     returns the exact claim shape from `0009` (`orgId` present for a tenant
     user, decodable via jwt.io or manually); wrong password and unknown email
     return byte-identical 401 bodies; rotation issues a different token and
     the original stops working; **presenting an already-rotated token
     returns 401 `RefreshTokenReuseDetected` and kills the token rotation had
     just issued too** — the family-wide blast radius, not just the reused
     token, confirmed by hand rather than only by the automated test.

### Phase 4 — tenant isolation, last because it depends on auth existing — **not started**
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
  ad hoc inside handlers. Implemented in Phase 2 (`ValidationBehavior`).

Settled while building Phase 2, recorded so they aren't re-litigated:

- **Dispatch interface is named `ISender`** — this codebase only needs
  one-way dispatch, no pub/sub notifications, so the narrower name is
  accurate. (It's also MediatR's name for its send-only interface; the
  shape is the mediator pattern itself, not borrowed code.)
- **No `FluentValidation.DependencyInjectionExtensions` package.** The
  assembly scan for handlers has to be hand-rolled regardless, so validators
  are picked up in that same pass rather than mixing a library-provided
  scanner with a hand-rolled one.
- **`ValidationBehavior` throws, never writes a response.** It throws
  `FluentValidation.ValidationException`; turning that into HTTP is
  `GlobalExceptionHandler`'s job alone, so error-shaping stays in one place.
  Each validator also gets its own `ValidationContext` — sharing one makes
  FluentValidation double-count failures across validators.
- **Reflection strategy in `Dispatcher`:** one `MakeGenericMethod().Invoke()`
  per dispatched request, no cached-delegate optimization. Clarity over
  micro-optimization at this stage; adding a `ConcurrentDictionary` cache
  later is purely additive, no public-contract change.
- **Behavior order is Logging outermost, Validation inside it**, so a request
  rejected by validation still produces start/completion log lines rather
  than vanishing from the logs.

## Still open

- **JWT claims shape** — **closed**, see `docs/decisions/0009`. `sub`, `email`,
  `orgId` (omitted for a SysAdmin), one `role` claim per role; HMAC-SHA256 with
  the key supplied from configuration only and validated at startup.

Nothing from WP-2 remains undecided. What Phase 4 inherits, rather than has to
decide:

- The `orgId` claim is the tenant accessor's input, and **its absence means
  SysAdmin**, never "tenant zero". A principal with no `orgId` and no
  `SysAdmin` role is malformed and should be rejected.
- Tenant scoping belongs **inside the existing policies** (or as a requirement
  alongside them), so no endpoint annotation has to change when it lands.
- `AuthenticationUserRepository` is the one sanctioned unfiltered read of
  `Users`. Adding the global query filter must not break it — that is why it
  was written with `IgnoreQueryFilters()` from the start rather than being
  retrofitted.
- `SysAdmin` is already excluded from `TenantMember`, so the "operator can
  administer but not read tenant content" line is drawn in one place and Phase
  4 can build on it rather than re-litigate it.
