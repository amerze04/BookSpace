_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-2 — Backend Skeleton, Auth & Tenancy — **In progress**
- [ ] Layered architecture: API / application / domain / infrastructure — the
      four projects and their inward-pointing references have existed since
      WP-0, and as of the mediator task `BookSpace.Application` finally holds
      real application code rather than being an empty shell. Left unchecked
      deliberately: worth a look with the mentor over whether this item means
      "the structure exists" (true now) or "every layer carries its intended
      responsibilities" (not until auth and the write paths land).
- [x] Wire EF Core to the WP-1 schema — `EnableRetryOnFailure` added (5
      retries, 1205 deadlock victim included) in
      `BookSpace.Infrastructure/DependencyInjection.cs`. No explicit
      `BeginTransaction` calls exist anywhere yet, so nothing needed
      rewriting onto `CreateExecutionStrategy().ExecuteAsync(...)` — future
      transactional code (e.g. refresh-token rotation) must use it, per
      CLAUDE.md §5.
- [x] Credential login issuing access token + rotating refresh token —
      `POST /auth/login` dispatches `LoginCommand` through the mediator to
      `LoginCommandHandler` (`Application/Features/Authentication/Login/`),
      which verifies the password against its PBKDF2 hash, checks `IsActive`
      and the owning org's status, then mints a 15-minute JWT plus a refresh
      token in a **new** `FamilyId` via the shared `TokenIssuer`. Claim shape
      and lifetimes fixed by `docs/decisions/0009`. Every credential failure —
      unknown email, wrong password, deactivated user, suspended org — returns
      the same `InvalidCredentials` reason code so login isn't an
      account-enumeration oracle; the real cause goes to the log with the
      correlation ID. `Jwt:SigningKey` is never committed and `JwtOptions` is
      `ValidateOnStart()`, so a missing or under-32-byte key fails the boot.
      **Verified manually (2026-08-26)** against a real running instance: login
      with a seeded member returns a decodable JWT with the exact claim set
      from `0009` (`orgId` present for a tenant user); wrong password and an
      unknown email return byte-identical 401 `InvalidCredentials` bodies
      (aside from the per-request `correlationId`/`traceId`); a malformed
      request returns 400 `ValidationFailed` with field errors, never reaching
      the handler.
- [x] Refresh-token rotation and reuse detection — `RefreshTokenCommandHandler`
      implements the five-case table in its own header comment: unknown hash →
      401; active → rotate (revoke old, set `ReplacedByTokenId`, new token in
      the **same** family inheriting the **original expiry** so the window stays
      absolute); expired → 401 revoking that token only, because expiry isn't
      theft; **already revoked → reuse detected, revoke the entire family**;
      inactive user/suspended org → 401 + family revoked (FR-2.4). Revocation
      is family-scoped, so one compromised session doesn't sign the user out of
      their other devices. Rotation is a single `SaveChangesAsync`, and
      `RefreshToken.RevokedAtUtc` is mapped as a **concurrency token** — EF
      appends `AND RevokedAtUtc IS NULL` to the revoking UPDATE, so two
      concurrent refreshes of the same token can't both mint a replacement (the
      loser gets `DbUpdateConcurrencyException` → 409, already mapped).
      `POST /auth/logout` revokes the family and returns 204 either way.
      Rationale in `docs/decisions/0011`.
      **Verified manually (2026-08-26):** login → rotate → the *original*
      token reused returns 401 `RefreshTokenReuseDetected`, and the token that
      rotation had just issued (previously valid) is also dead immediately
      after — confirming the whole family dies, not just the reused token.
- [x] RBAC (SysAdmin, TenantAdmin, Approver, Member) — four named policies in
      `Api/Authorization/AuthorizationPolicies.cs` (`SysAdminOnly`,
      `TenantAdmin`, `Approver`, `TenantMember`) plus
      `FallbackPolicy = RequireAuthenticatedUser`, so a controller added later
      is **protected unless it opts out** with `[AllowAnonymous]` — opt-in
      would leave every new endpoint one forgotten attribute away from public.
      `HealthController` and `AuthController` are the two opt-outs.
      `TenantMember` deliberately **excludes** SysAdmin by requiring the
      `orgId` claim (PRD §2: the Platform Operator must never see tenant
      booking content in routine operation), so SysAdmin is a separate axis,
      not the top of a ladder. `docs/decisions/0012`.
      **Note:** roles come from the token, so a role change takes effect at the
      next refresh — the same boundary FR-2.4 uses.
- [x] Structural tenant isolation (§4.2) — all three mechanisms land in
      `BookSpace.Infrastructure.Persistence`. **Global query filters**:
      `BookSpaceDbContext.OnModelCreating` adds
      `HasQueryFilter(x => x.OrgId == _currentTenant.OrgId)` for `Users`,
      `Resources`, `Bookings`, where `ICurrentTenant`
      (`Application/Abstractions/ICurrentTenant.cs`, implemented by
      `Api/Tenancy/HttpContextCurrentTenant.cs` reading the `orgId` claim from
      `0009`) is constructor-injected — the standard EF Core multi-tenancy
      pattern. EF's null-safe translation gives the right fail-closed
      behavior for free: no tenant context hides all `Resources`/`Bookings`
      rows and all but SysAdmin `Users` rows. **`SaveChanges*` enforcement**:
      turned out to be validation, not assignment — `ITenantOwned.OrgId` has
      no setter and every entity already requires it at construction, so
      `BookSpaceDbContext` instead throws `TenantIsolationViolationException`
      (`Domain/Common/`) if an `Added`/`Modified` `ITenantOwned` entity's
      `OrgId` doesn't match the current tenant, no-opping when there is no
      current tenant (SeedData). **RLS**: `TenantSessionContextInterceptor`
      calls `sp_set_session_context` on every `ConnectionOpened`
      (`TenantInit=1`, `OrgId`, `TenantBypass`), wired via
      `DependencyInjection.cs`'s `(IServiceProvider, options)` `AddDbContext`
      overload so it resolves the request's own `ICurrentTenant`. The
      `AddTenantIsolationRls` migration adds a `Security` schema, one shared
      inline predicate function, and a filter-only `SECURITY POLICY` on all
      three tables. `AuthenticationUserRepository` — the one sanctioned
      `IgnoreQueryFilters()` exception — now also enters
      `TenantBypassScope` (`AsyncLocal`-backed), since `IgnoreQueryFilters()`
      only skips the EF-generated WHERE clause and has no effect on RLS,
      which the engine enforces independently. Both reinterpretations
      (validation-not-assignment; the `TenantInit`/`TenantBypass` design) are
      recorded in `docs/decisions/0013-tenant-isolation-mechanism.md`.
      **Verified**: `dotnet test` — 257 tests (192 unit incl. 11 new EF
      InMemory tests over `ChangeTracker`/filter logic, 65 integration incl. 7
      new `TenantIsolation` tests against real SQL Server) all pass. The
      integration suite proves isolation at two independent levels: through
      the real HTTP pipeline with a real authenticated principal (query
      filter + RLS together, as production traffic hits them), and via a raw
      `SqlConnection` with no EF involved at all — confirming a connection
      with no session context, or `OrgId` set but `TenantInit` omitted, sees
      zero rows even though rows physically exist, and a connection scoped to
      Acme's real `OrgId` sees exactly Acme's 2 resources / 4 users, never
      Globex's. Manually re-confirmed against the dev database via `sqlcmd`
      (2026-08-27): no session context → 0 `Resources` rows; `TenantBypass=1`
      → all 4; a real Acme `OrgId` with `TenantBypass=0` → exactly 2
      resources and 4 users. **The one gap this left is now closed**: no
      `Bookings` rows were seeded (§4.1 — `dbo.CreateBooking` didn't exist), so
      the `Bookings` cross-tenant test was a smoke check only — the filter
      clause built and returned empty without throwing, which proves nothing
      about a leak. **WP-4 Phase 3 (2026-09-08)** seeds six bookings and
      replaced it with the real thing: the cross-tenant 404 / own-tenant 200
      pair over an `Id`-only probe, an exact-count list test, and a raw
      connection seeing zero `Bookings` rows with no session context and
      exactly Acme's three with it. Confirmed able to fail — bypassing *both*
      mechanisms in the probe makes the cross-tenant test return 200.
- [x] Serilog structured logging, correlation ID per request —
      `Serilog.AspNetCore` + `Serilog.Settings.Configuration` wired in
      `Program.cs` (bootstrap logger, `appsettings`-driven sinks/levels);
      `CorrelationIdMiddleware` (`BookSpace.Api/Middleware/`) reads/generates
      an `X-Correlation-Id` header, sets `HttpContext.TraceIdentifier`, and
      pushes it into Serilog's `LogContext` so every log line for a request —
      controller code and the `UseSerilogRequestLogging()` summary line alike
      — carries it with no per-call-site plumbing. Verified manually (console
      output + response header, both with and without an inbound header) and
      via two new unit tests in `BookSpace.UnitTests/Middleware/`.
- [x] Global exception handler → `ProblemDetails` with correlation ID —
      `GlobalExceptionHandler` (`BookSpace.Api/ExceptionHandling/`) implements
      `IExceptionHandler`, registered via `AddExceptionHandler<>()` and run
      through `app.UseExceptionHandler()` (placed inside
      `UseSerilogRequestLogging()` so the exception is logged exactly once,
      at the boundary, never twice). `AddProblemDetails()` is configured to
      stamp `extensions.correlationId` from `HttpContext.TraceIdentifier`
      (already set by `CorrelationIdMiddleware`) onto every `ProblemDetails`
      response app-wide. Scope is deliberately a safety net, not the full
      AC: any unhandled exception → 500, `DbUpdateConcurrencyException` →
      409 (`CLAUDE.md` §5); logged at `LogError` for 5xx, `LogWarning`
      otherwise. Verified with unit tests in
      `BookSpace.UnitTests/ExceptionHandling/`.
      **Deferred, not this task** — see the comment above
      `GlobalExceptionHandler.Map(...)` and `docs/wp2-plan.md` Phase 1 item
      3: map booking rejection reason codes once that write path exists.
      `FluentValidation.ValidationException` mapping landed with the
      mediator task below.
- [ ] Map domain/validation errors to clean, consistent problem responses —
      `FluentValidation.ValidationException` → 400 with per-field errors (see
      the mediator entry below) and `AuthenticationException` → 401 carrying
      the handler's own reason code (`InvalidCredentials`,
      `InvalidRefreshToken`, `RefreshTokenExpired`,
      `RefreshTokenReuseDetected`, `AccountInactive`) are both done. Left
      unchecked: booking-rejection reason codes (§6) are still deferred — no
      write path exists yet.
- [x] Hand-written mediator (no MediatR) with a pipeline for cross-cutting
      behaviors (logging, validation) — `BookSpace.Application/Messaging/`
      defines the dispatcher shape (`IRequest<TResponse>`, `IRequestHandler`,
      `IPipelineBehavior`, and the public `ISender` controllers depend on)
      and `Dispatcher` (`Messaging/Dispatcher.cs`), which resolves the
      closed-generic handler/behaviors for a request's runtime type via one
      reflective bridge call, then composes the behavior chain around the
      handler in reverse registration order. `Messaging/Behaviors/` has
      `LoggingBehavior` (start/completion + elapsed ms, riding the ambient
      Serilog `LogContext` `CorrelationIdMiddleware` already populates — no
      extra plumbing) and `ValidationBehavior` (resolves
      `IEnumerable<IValidator<TRequest>>`, throws
      `FluentValidation.ValidationException` on failure so mapping stays
      `GlobalExceptionHandler`'s job alone). `Application/DependencyInjection.cs`
      (`AddApplication()`, called from `Program.cs`) does one reflection pass
      registering both `IRequestHandler<,>` and `IValidator<>`
      implementations, then registers the two behaviors — Logging first so
      it's outermost, Validation second — and `ISender` itself, all `Scoped`.
      `GlobalExceptionHandler.Map` gained the `ValidationException` → 400
      case plus a per-field `errors` extension. Proved end-to-end with a
      temporary `PingCommand`/`PingCommandHandler`/`PingCommandValidator`
      (`Application/Features/Ping/`) dispatched from `PingController` —
      **temporary, delete both once login is the first real handler**, per
      `docs/wp2-plan.md`'s explicit sequencing note to prove the pipeline
      before building against it. Verified with unit tests in
      `BookSpace.UnitTests/Messaging/` (dispatch, ordering, short-circuiting,
      both behaviors) and extended `ExceptionHandlerTests`, plus a manual HTTP
      round trip (2026-08-26): valid `POST /ping` → 200 with the handler's log
      line present; empty message → 400 carrying `reasonCode:
      "ValidationFailed"` and per-field `errors`, with the handler's log line
      absent — i.e. validation short-circuited before the handler — and the
      inbound `X-Correlation-Id` on every log line including the exception
      handler's.
      **Ping slice is now deleted** — `Application/Features/Ping/` and
      `Api/Controllers/PingController.cs` were removed as part of the auth task,
      as planned, since login is now the first real handler.
      **Gotcha for later:** a validator is only discovered if it lives in the
      `BookSpace.Application` assembly *and* is typed against the exact
      concrete request type (`AbstractValidator<TheCommand>`). Generics are
      invariant, so `IValidator<SomeBase>` never satisfies
      `IValidator<TheCommand>`, and nothing checks at startup that a command
      has a validator — a missing or mistyped one is a silent pass-through.

Acceptance criteria: see the source doc — login/refresh/rotation/reuse
detection, hashed passwords, tenant isolation under a forged identifier,
correlation ID on every log line, clean problem responses on unhandled
exceptions, and controllers that only dispatch through the mediator.
**As of 2026-08-27: all WP-2 acceptance criteria are met**, including tenant
isolation under a forged identifier (Phase 4). Login/refresh/
rotation/reuse-detection, hashed passwords, correlation IDs, problem
responses, mediator-only controllers, and structural tenant isolation are all
implemented, covered by 257 automated tests (192 unit, 65 integration against
real SQL Server), and the login/rotation/reuse-detection paths additionally
verified by hand against a running instance.

