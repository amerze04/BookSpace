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
