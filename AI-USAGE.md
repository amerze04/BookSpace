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
