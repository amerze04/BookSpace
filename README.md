# BookSpace

BookSpace is a multi-tenant SaaS platform for booking shared organizational
resources (rooms, equipment, vehicles, lab slots). Each tenant is an
isolated workspace. Administrators publish resources with availability and
governance rules; members create one-off or recurring bookings against live
availability, with a hard guarantee of **zero double-bookings under
concurrent load**.

This is a from-scratch rebuild of an earlier attempt — the design (schema,
PRD, architectural decisions) carries over unchanged, only the build order
was reset to start from proper project foundations. See
[`docs/RESTART_NOTES.md`](docs/RESTART_NOTES.md) for why, and
[`CLAUDE.md`](CLAUDE.md) for the full set of working conventions and hard
rules for this codebase.

## Documentation

| Doc | What it's for |
|---|---|
| [`CLAUDE.md`](CLAUDE.md) | Working conventions, hard rules, decisions index, build roadmap |
| [`docs/BookSpace_PRD_v1.docx`](docs/BookSpace_PRD_v1.docx) | Product requirements (FR-*/AC-* IDs) |
| [`docs/Work Packages - Week 1 and 2.docx`](<docs/Work Packages - Week 1 and 2.docx>) | The work packages this build follows, in order |
| [`docs/bookspace-schema-v2.sql`](docs/bookspace-schema-v2.sql) | Database schema — source of truth |
| [`docs/decisions/`](docs/decisions/) | Resolved architectural decisions, with rationale |
| [`docs/RESTART_NOTES.md`](docs/RESTART_NOTES.md) | Why this repo restarted, what carried over |

## Stack

| Layer | Choice |
|---|---|
| Database | SQL Server |
| Data access | EF Core (code-first migrations) |
| Backend | .NET / ASP.NET Core Web API |
| Frontend | Angular |
| Background jobs | Scheduled worker (idempotent) |

## Repository layout

```
backend/                      .NET solution (see backend/README below)
  BookSpace.slnx
  src/
    BookSpace.Domain/
    BookSpace.Application/
    BookSpace.Infrastructure/
    BookSpace.Api/
  tests/
    BookSpace.UnitTests/
    BookSpace.IntegrationTests/
frontend/                     Angular app
docs/                         PRD, work packages, schema, decision records
```

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) or later
- [Node.js 24 LTS](https://nodejs.org/) (includes npm)
- [Angular CLI](https://angular.dev/tools/cli): `npm install -g @angular/cli`
- SQL Server — a local instance (SQL Server Express/Developer, or a named
  instance) or a container. The connection string in
  `backend/src/BookSpace.Api/appsettings.json` targets `localhost\SQLEXPRESS`
  by default; override it for your environment via
  `appsettings.Development.json` or the `ConnectionStrings__BookSpaceDb`
  environment variable rather than committing a machine-specific value.

## Running it locally

### 1. Database

Create an empty `BookSpace` database on your SQL Server instance:

```sh
sqlcmd -S "localhost\SQLEXPRESS" -Q "CREATE DATABASE BookSpace" -C
```

(Adjust `-S` for your instance name. `-C` trusts the server certificate for
a local dev connection.)

### 2. Backend

```sh
cd backend
dotnet build
dotnet test
dotnet run --project src/BookSpace.Api
```

The API listens on the URL printed at startup (see
`src/BookSpace.Api/Properties/launchSettings.json`, `http://localhost:5270`
by default). Confirm the API is up and can reach the database:

```sh
curl http://localhost:5270/health
curl http://localhost:5270/health/db
```

`/health/db` needs SQL Server running; `/health` does not.

To exercise the mediator pipeline end to end there's a temporary
`POST /ping` endpoint (removed once login becomes the first real handler):

```sh
curl -i -X POST http://localhost:5270/ping \
  -H "Content-Type: application/json" \
  -H "X-Correlation-Id: my-test" \
  -d '{"message":"hello"}'
```

A valid `message` returns 200; an empty one returns a 400 `ProblemDetails`
with `reasonCode: "ValidationFailed"` and per-field `errors`.

**On Windows PowerShell:** use `curl.exe`, not `curl` — the latter is an
alias for `Invoke-WebRequest` and rejects `-X`/`-d`. PowerShell 5.1 also
strips the inner double quotes out of a JSON body on the way to a native
executable, so escape them (`-d '{\"message\":\"hello\"}'`) or pass the body
from a file (`-d "@body.json"`).

Starting the API with `--no-launch-profile` runs it in Production, which
skips the Development-only seed step — useful for testing endpoints that
don't touch the database without needing SQL Server up.

### 3. Frontend

```sh
cd frontend
npm install
npm start
```

Serves the Angular app at `http://localhost:4200` by default.

## Branching

`main` is protected by convention — no direct commits. Work happens on
feature branches (`feature/<short-description>`), merged via PR/review.

## AI usage

This project uses an AI coding assistant as part of delivery. See
[`AI-USAGE.md`](AI-USAGE.md) for a log of what it was asked to do, per work
package, and what was reviewed by hand.
