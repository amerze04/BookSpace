# AI Usage Log — BookSpace

This project uses an AI coding assistant (Claude Code) as part of the
delivery process. This log records where and how it was used, so the
reasoning behind AI-assisted decisions stays traceable and defensible.

Add an entry per work package (or per significant session). Keep entries
factual and specific — "used AI to write the whole thing" is not useful;
"asked it to draft the EF Core configurations against the existing schema,
then reviewed the constraint names against bookspace-schema-v2.sql by hand"
is.

---

## Entry template

```
### WP-N — <short title>
**Date:** YYYY-MM-DD
**Tool:** Claude Code (model: <name>)

**What I asked for:**
<prompt summary — what task was delegated>

**What it produced:**
<files/artifacts generated or modified>

**What I reviewed / changed:**
<what you verified by hand, what you corrected or rejected, and why>

**What I did not use it for:**
<anything you deliberately did yourself, if relevant>
```

---

## WP-0 — Project setup & foundations
**Date:** 2026-08-20
**Tool:** Claude Code (model: claude-sonnet-5)

**What I asked for:**
Consolidate all context from a prior implementation attempt
(`BookSpace_initial/`, since removed) — the PRD, database schema, ERD
mentor-feedback thread, and all 8 resolved architectural decisions — into
this repo's `docs/`, and document why the project restarted
(`docs/RESTART_NOTES.md`). Then complete WP-0 from
`docs/Work Packages - Week 1 and 2.docx`: repo layout, backend solution
scaffold, frontend scaffold, local SQL Server connectivity, `.gitignore`,
`.editorconfig`, this file.

**What it produced:**
- `docs/RESTART_NOTES.md`, root `CLAUDE.md` (carried forward + updated build
  roadmap), copied PRD/schema/decisions/ERD-feedback docs into `docs/`.
- `backend/` — `.slnx` solution, `BookSpace.Domain/Application/Infrastructure/Api`
  class libraries wired with the layering rules from `CLAUDE.md` §3, plus
  `BookSpace.UnitTests`/`BookSpace.IntegrationTests`. A `HealthController`
  with a `/health/db` endpoint that opens a real `SqlConnection` against the
  local SQL Server instance.
- `frontend/` — Angular app scaffolded via `ng new` (routing, SCSS).
- Root `.gitignore`, `.editorconfig`, this file.
- Created the local `BookSpace` database on the machine's SQL Server Express
  instance (`localhost\SQLEXPRESS`).

**What I reviewed / changed:**
- Confirmed `dotnet build` succeeds for the full backend solution.
- Confirmed `ng build` succeeds for the frontend.
- Ran the API and hit `GET /health/db` to confirm it actually opens a
  connection to the real local SQL Server instance, not just that the code
  compiles.
- Chose not to carry over `bookspace-schema-v2-vp-import.sql` (the old repo
  already documented it as deprecated) or any of the previously-written
  application code — see `docs/RESTART_NOTES.md` for the reasoning.

**What I did not use it for:**
- Git repository / remote setup and the initial commit — left for me to do
  directly, deliberately, so the account and history are mine from the
  first commit.

---

## WP-2 — Serilog structured logging + correlation ID
**Date:** 2026-08-24
**Tool:** Claude Code (model: claude-sonnet-5)

**What I asked for:**
Plan out WP-2 Phase 1's logging item from `docs/wp2-plan.md` (mentor-approved
sequencing) before touching code — file-level design first, reviewed and
approved, then implementation.

**What it produced:**
- A file-level implementation plan (design phase used a Plan subagent),
  covering package choice, middleware design, `Program.cs` wiring, and
  config changes — written to a plan file for review before any code
  changed.
- `backend/src/BookSpace.Api/Middleware/CorrelationIdMiddleware.cs` — reads
  an inbound `X-Correlation-Id` header (echoes it) or generates one, sets
  `HttpContext.TraceIdentifier`, and pushes it into Serilog's `LogContext`
  so every log line for a request carries it automatically.
- `Program.cs` — Serilog bootstrap logger, `UseSerilog` reading from
  `appsettings`, a `HostAbortedException` filter (needed so `dotnet ef`
  commands don't log a spurious fatal crash), correlation middleware +
  `UseSerilogRequestLogging()` first in the pipeline, `public partial class
  Program` for future `WebApplicationFactory`-based tests.
- `appsettings.json` / `appsettings.Development.json` — `Serilog` section
  replacing the old `Logging` section (console sink; `Information`/`Warning`
  in the base config, more verbose in Development).
- One log line added to `HealthController.GET /health`, added specifically
  as a manual-verification vehicle (not a feature requirement).
- Two new unit tests (`CorrelationIdMiddlewareTests`) plus the `.csproj`
  wiring (`FrameworkReference`/`ProjectReference`) they needed.

**What I reviewed / changed:**
- Read every generated file (middleware, `Program.cs`, config, controller,
  test) before accepting the plan and again after implementation.
- Ran `dotnet build` and `dotnet test` — 100 unit tests + 18 integration
  tests, all passing.
- Ran the API by hand and used `curl` against `/health`, with and without an
  inbound `X-Correlation-Id` header, to confirm the ID shows up consistently
  across every log line for a request and on the response header, and that
  two separate requests get two different generated IDs.
- Investigated and explained (rather than treating as a bug) why ASP.NET
  Core's own "Request starting"/"Request finished" lines fall outside the
  correlation scope — a framework-level ordering fact, not a defect.
- Chose console-only logging (no rolling file sink), reasoning from the
  Azure-hosting preference already in `CLAUDE.md` §2 — flagged as an easy
  reversal if local file logs turn out to be needed later.

**What I did not use it for:**
- The global exception handler (WP-2's next item) — deliberately kept
  out of scope for this session, even though it will read the
  `TraceIdentifier` this work already sets.

---

## WP-2 (Phase 3) — Credential login, refresh-token rotation, RBAC
**Date:** 2026-08-26
**Tool:** Claude Code (model: claude-opus-5)

**What I asked for:**
Read the PRD, work packages, WP-2 plan and decision log first and report back
on where the build stood, with no code — then, in a second pass, plan and
implement the auth task from WP-2: credential login issuing a short-lived
access token plus a rotating refresh token, refresh rotation with reuse
detection, and RBAC over the four roles. I asked it to identify the open
design questions and pick the answers itself for all of them **except** the
email-uniqueness one, which I decided: emails become globally unique, so a
user is only ever tied to one tenant.

**What it produced:**
- Flagged six open questions before writing anything, one of which
  (`#2`) turned out to be a genuine gap in the WP-1 schema rather than an
  unmade decision — `UX_Users_Org_Email` was `(OrgId, Email)` filtered on
  `OrgId IS NOT NULL`, so the same email could exist in two tenants *and*
  SysAdmin rows had no email uniqueness at all. That's the one I decided
  myself.
- Four decision records: `docs/decisions/0009` (JWT claims + lifetimes),
  `0010` (global email uniqueness), `0011` (refresh-token hashing, rotation,
  reuse detection), `0012` (RBAC enforcement model).
- `BookSpace.Application` — `Abstractions/` (password hasher, refresh-token
  factory, access-token service, clock, and the two auth repositories) and
  `Features/Authentication/` (`Login`, `Refresh`, `Logout`, the shared
  `TokenIssuer`, `AuthenticationException` + reason codes).
- `BookSpace.Infrastructure` — `Security/` (`JwtOptions`,
  `JwtAccessTokenService`, `PasswordHasherAdapter`, `RefreshTokenFactory`,
  `SystemClock`, `BookSpaceClaims`) and two repositories. Seed data now
  stores real PBKDF2 hashes instead of the WP-1 placeholder.
- `BookSpace.Api` — `AuthController`, `AuthorizationPolicies` (four named
  policies + a deny-by-default fallback), JwtBearer wiring in `Program.cs`,
  and an `AuthenticationException` → 401 case in `GlobalExceptionHandler`.
- The email-uniqueness migration, and the `Ping` proof-of-concept slice
  deleted as the WP-2 plan required.
- Unit tests for the token service, hashers, validators and all three
  handlers, plus integration tests over real HTTP against real SQL Server.

**What I reviewed / changed:**
- Made the email-uniqueness call myself (see above) rather than letting it
  pick, since it changes the data model.
- Reviewed the reasoning in each of the four decision records — particularly
  `0011`'s justification for SHA-256 on refresh tokens while passwords use
  PBKDF2, since that inconsistency is the obvious thing to be challenged on
  in review, and the answer (deterministic lookup by hash + 256 bits of
  entropy means a slow KDF buys nothing) is one I need to be able to give.
- Checked the deny-by-default `FallbackPolicy` really does what it claims by
  reading the test that asserts an unannotated endpoint returns 401.
- Verified `Jwt:SigningKey` is absent from `appsettings.json` and that the
  app refuses to start without it.

**What I did not use it for:**
- Structural tenant isolation (WP-2 Phase 4) — deliberately left out, though
  the `orgId` claim and the named `IgnoreQueryFilters()` repository were
  shaped for it.
- Committing anything. As always, the working tree was left for me to review
  and commit myself.

---

## WP-2 (Phase 3 follow-up) — Concept walkthrough and manual verification
**Date:** 2026-08-26
**Tool:** Claude Code (model: claude-sonnet-5)

**What I asked for:**
A from-scratch teaching walkthrough of the WP-2 Phase 3 auth code — what a
JWT and refresh token actually are, how password hashing works, and how
every file involved fits together — split into four digestible units with
room for questions between each. Then two manual tests to run by hand: a
happy path (login, decode the JWT, rotate, confirm reuse detection) and a
non-happy path (wrong password vs. unknown email, confirm identical
responses).

**What it produced:**
- The four-unit walkthrough (fundamentals/login, token-issuing internals,
  rotation/reuse detection, RBAC/pipeline wiring) plus a follow-up answer on
  why PBKDF2 verification works despite never hashing the same input twice.
- Manual test scripts, revised twice: an initial `curl`/`curl.exe` version
  that didn't work in PowerShell (wrong line-continuation syntax, then the
  known PowerShell-mangles-native-argv-quoting problem the README already
  flagged), replaced with a verified `Invoke-RestMethod`-based version after
  testing it directly rather than guessing a second time.
- Diagnosed and fixed a `DROP DATABASE`/`CREATE DATABASE` failure on the dev
  machine (drop succeeded but left orphaned `.mdf`/`.ldf` files, so create
  failed) by recreating the database with explicit different physical file
  names rather than touching files under SQL Server's locked-down data
  directory.
- Follow-up doc updates: `README.md` (a real gap — migrations were never
  documented as a required step, so a fresh/reset database would fail to
  seed; also replaced the PowerShell curl guidance with the verified
  `Invoke-RestMethod` approach, and added the orphaned-file troubleshooting
  note), `CLAUDE.md` and `docs/wp2-plan.md` (recorded that login/rotation/
  reuse-detection are now manually verified against a running instance, not
  just covered by automated tests, plus the two operational gotchas above
  for the next session).

**What I reviewed / changed:**
- Ran the corrected `Invoke-RestMethod` commands myself before handing them
  over a second time, rather than repeat the earlier mistake of proposing
  unverified shell syntax.
- Confirmed the database fix worked (`sys.databases` showed `BookSpace`
  `ONLINE`, zero tables before migration) before telling the user to
  continue.
- Read the current README/CLAUDE.md/wp2-plan.md content before editing each,
  since parts had already been revised since the file was first read.

**What I did not use it for:**
- No feature code changed in this follow-up — it was explanation, manual
  testing, and documentation only.
- Did not delete or otherwise touch the orphaned `.mdf`/`.ldf` files, since
  they're under SQL Server's data directory and out of reach without service
  account permissions; noted as a harmless leftover in the README instead.

---

## WP-2 (Phase 3 follow-up, continued) — Correcting the "orphaned files" diagnosis
**Date:** 2026-08-26
**Tool:** Claude Code (model: claude-sonnet-5)

**What I asked for:**
Whether it was safe to delete the "orphaned" `.mdf`/`.ldf` files from the
previous entry and stop needing to document the workaround.

**What it produced — and corrected:**
The earlier entry's diagnosis was wrong, and I want that on the record rather
than quietly fixed. I'd checked `sys.databases WHERE name = 'BookSpace'` and
concluded the files were unowned; I never checked whether a *different*
database owned them under a different name. Asked to confirm before deleting,
`sys.master_files` showed `BookSpace.mdf`/`BookSpace_log.ldf` belonged to a
live database, `BookSpace_initial` — almost certainly the pre-WP-0-restart
attempt from `docs/RESTART_NOTES.md`, renamed rather than dropped at the time
(`ALTER DATABASE ... MODIFY NAME` doesn't touch physical filenames, which is
why a database under an unrelated name was still sitting on the default
`BookSpace.*` files). Confirmed it held only an applied-migrations schema and
zero data rows before recommending anything.

**What I reviewed / changed:**
- Did not delete the files directly, on the strength of my first read of
  `sys.databases` alone — the broader `sys.master_files` check is what caught
  the mistake before it became a destructive one.
- Asked for explicit confirmation before running `DROP DATABASE
  BookSpace_initial`, even though the evidence (empty schema, restart notes)
  pointed one way, since it predated this session and was irreversible.
- After the drop, corrected the false "Windows can leave `.mdf`/`.ldf` files
  behind after a successful drop" explanation in `README.md` and
  `docs/wp2-plan.md` — replaced with the real mechanism and a
  `sys.master_files`-based diagnostic for next time, rather than leaving a
  plausible-sounding but incorrect root cause in the permanent docs.

**What I did not use it for:**
- Did not touch anything else in `BookSpace_initial` beyond confirming it was
  empty before the drop the user approved.
