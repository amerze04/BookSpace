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

**Resetting the database:** `DROP DATABASE BookSpace; CREATE DATABASE BookSpace`
can fail on `CREATE` with `Msg 5170 ... Cannot create file '...BookSpace.mdf'
because it already exists`, even though `sys.databases` confirms the drop
itself succeeded. Don't assume this is a leftover from the drop you just ran —
check whether some *other* database, under a different name, owns those exact
files (a rename via `ALTER DATABASE ... MODIFY NAME` changes the logical name
but never the physical file names, so an old database can sit there for
months looking unrelated while still holding the default filenames a fresh
`CREATE DATABASE BookSpace` wants):

```sh
sqlcmd -S "localhost\SQLEXPRESS" -C -Q "SELECT DB_NAME(database_id) AS OwningDatabase, physical_name FROM sys.master_files WHERE physical_name LIKE '%BookSpace%'"
```

If that turns up a database you don't need — this happened once with a
`BookSpace_initial` left over from the pre-WP-0 restart (`docs/RESTART_NOTES.md`),
empty schema, no data — dropping *that* database frees the filenames properly,
no workaround needed. Only reach for `CREATE DATABASE ... ON PRIMARY (..., FILENAME = ...)`
with an explicit alternate path if the colliding files turn out to be
genuinely unowned orphans (nothing in `sys.master_files` claims them) that you
can't otherwise remove.

### 2. JWT signing key (required — the API will not start without it)

Access tokens are signed with a symmetric key that is deliberately **not**
committed (see `CLAUDE.md` §4.4). `appsettings.json` carries the issuer,
audience, and token lifetimes, but no key. Startup validates the key and fails
fast if it is missing or shorter than 32 characters, rather than booting with
something forgeable.

Set it once via user-secrets:

```sh
cd backend/src/BookSpace.Api
dotnet user-secrets init
dotnet user-secrets set "Jwt:SigningKey" "replace-me-with-32+-random-characters"
```

Outside development, supply it as the `Jwt__SigningKey` environment variable
instead (note the double underscore).

### 3. Backend

The database has no tables yet — migrations aren't applied automatically on
startup, so this is a required one-time (or per-reset) step, not optional
polish:

```sh
cd backend
dotnet tool install --global dotnet-ef   # if `dotnet ef --version` doesn't already work
dotnet ef database update --project src/BookSpace.Infrastructure --startup-project src/BookSpace.Api
```

This needs the JWT signing key from step 2 to already be set — `dotnet ef`
runs enough of `Program.cs` to hit the `ValidateOnStart()` check on
`JwtOptions`, so it fails the same way `dotnet run` would without a key.

```sh
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

### Signing in

Running in Development seeds a multi-tenant dataset. Every seeded account
shares the password **`Passw0rd!`** (`SeedData.SeedPassword`) — development
convenience only, never a real environment. The accounts are:

| Email | Role |
|---|---|
| `sysadmin@bookspace.local` | SysAdmin (no tenant) |
| `admin@acme.test` / `admin@globex.test` | TenantAdmin |
| `approver@acme.test` / `approver@globex.test` | Approver |
| `member1@…`, `member2@…` | Member |

```sh
# Log in — returns accessToken, expiresIn, refreshToken
curl -i -X POST http://localhost:5270/auth/login \
  -H "Content-Type: application/json" \
  -H "X-Correlation-Id: my-test" \
  -d '{"email":"member1@acme.test","password":"Passw0rd!"}'

# Rotate — the old refresh token is revoked and replaced
curl -i -X POST http://localhost:5270/auth/refresh \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"<the refreshToken from above>"}'

# End the session (revokes the whole token family)
curl -i -X POST http://localhost:5270/auth/logout \
  -H "Content-Type: application/json" \
  -d '{"refreshToken":"<current refreshToken>"}'
```

Presenting a refresh token that has **already been rotated** returns 401 with
`reasonCode: "RefreshTokenReuseDetected"` and revokes every token in that
family — the FR-2.2 stolen-token guarantee. Bad credentials return 401
`InvalidCredentials`; a malformed request is rejected by the validation
pipeline before the handler runs, as 400 `ValidationFailed` with per-field
`errors`. Every response carries a `correlationId`.

Endpoints are protected by default — a request with no token gets 401, and one
with a token lacking the required role gets 403. Only `/health*` and `/auth/*`
are anonymous.

**On Windows PowerShell, use `Invoke-RestMethod`, not `curl`/`curl.exe`.**
Bare `curl` is a PowerShell alias for `Invoke-WebRequest` and rejects
`-X`/`-d`. `curl.exe` (the real binary) avoids that, but hits a worse problem:
PowerShell rewrites how a quoted string reaches a *native* executable's
argument list, so the inner `"` characters in a JSON body get silently
stripped or mangled — confirmed live (2026-08-26): the same `-d '{"email":
...}'` that works from bash arrived at the API missing every inner quote,
producing a 400 `ValidationFailed` for looking like malformed JSON. Escaping
the quotes or reading the body from a file works around it, but
`Invoke-RestMethod` sidesteps the whole class of problem — it's a PowerShell
cmdlet, not a native binary, so `-Body` takes the string directly with no
argv reconstruction in between:

```powershell
$body = @{ email = "member1@acme.test"; password = "Passw0rd!" } | ConvertTo-Json
$login = Invoke-RestMethod -Uri http://localhost:5270/auth/login -Method Post -ContentType "application/json" -Body $body
$login | Format-List   # default table view truncates columns to terminal width — Format-List shows everything

# Rotate
$refreshBody = @{ refreshToken = $login.refreshToken } | ConvertTo-Json
Invoke-RestMethod -Uri http://localhost:5270/auth/refresh -Method Post -ContentType "application/json" -Body $refreshBody

# A failure response (4xx/5xx) throws in PowerShell — read the body like this:
try {
    Invoke-RestMethod -Uri http://localhost:5270/auth/refresh -Method Post -ContentType "application/json" -Body $refreshBody
} catch {
    $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
    $reader.ReadToEnd() | ConvertFrom-Json | Format-List
}
```

Starting the API with `--no-launch-profile` runs it in Production, which
skips the Development-only seed step — useful for testing endpoints that
don't touch the database without needing SQL Server up.

### 4. Frontend

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
