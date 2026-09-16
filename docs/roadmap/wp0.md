_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-0 — Project Setup & Foundations — **Done** (2026-08-20)
- [x] Create private Git repo; main + feature-branch workflow — **skipped
      by owner's choice**, see Notes.
- [x] README describing the project and how to run it locally.
- [x] Scaffold solution structure: backend (`.NET`) + frontend (Angular).
- [x] Set up SQL Server locally and confirm connectivity.
- [x] `.gitignore`, `.editorconfig`, basic solution conventions.
- [x] `AI-USAGE.md` with headers to fill in throughout.

Acceptance criteria:
- [x] Repo clones and both projects build from a clean checkout following
      only the README — verified: `dotnet build`/`dotnet test` and
      `ng build` both pass.
- [ ] Branch protection / no-direct-to-main convention — documented as a
      convention in the README, but there's no repo yet to enforce it in.
- [x] Database connection confirmed from the backend — `GET /health/db`
      opens a real `SqlConnection` to the local `BookSpace` database.

Notes: git/remote setup was explicitly deferred to the repo owner rather
than done by the assistant — not a gap in scope, a deliberate choice.

