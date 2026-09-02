# Postman — manual verification for WP-3

Two files, both committed:

- `BookSpace.postman_collection.json` — 38 requests in five folders, every one
  carrying assertions.
- `BookSpace.postman_environment.json` — base URL and the seeded accounts. No
  secrets: the only password in it is `SeedData.SeedPassword`, which exists in
  development only.

The automated suite (445 unit + 182 integration) stays the source of truth. This
is for the things a client shows better than a test log: response shapes, error
bodies, and demonstrating the acceptance criteria to someone watching.

**Status (2026-09-02):** the *walkthrough below* was executed by hand against a
running instance and everything behaved as documented. The **collection JSON has
not itself been run** — it was written from the code, not from a green runner
pass. Treat a first Collection Runner run as debugging the collection, not as
testing the API; if a request fails there, suspect the script before the endpoint.

---

## Running it

1. Start the API in Development:

   ```
   cd backend/src/BookSpace.Api
   dotnet run
   ```

   Development seeds the database on startup, so the accounts below exist.

2. In Postman: **Import** both files, then select the **BookSpace — Local**
   environment (top right).

3. Either:
   - **Run the whole collection** (Collection Runner, or ⌘/Ctrl-click the
     collection → Run). Every request asserts, so a green run is a pass; or
   - **Walk it by hand**, top to bottom.

**Order matters.** Folder `00` captures the tokens and user ids everything else
needs, and folder `01` creates the resource folders `02`–`05` operate on. Running
`02` on its own will 404.

URLs: `http://localhost:5270`, or `https://localhost:7079` if you turn off SSL
verification for the dev certificate (Settings → General).

---

## Seeded accounts

All use password `Passw0rd!` (`SeedData.SeedPassword`, development only).

| Email | Role | What it is for here |
|---|---|---|
| `admin@acme.test` | TenantAdmin | the working identity — creates, edits, schedules, assigns |
| `approver@acme.test` | Approver | the person assigned as an approver, and proof an Approver is still a non-admin |
| `member1@acme.test` | Member | reads, and proving a non-admin write is refused |
| `admin@globex.test` | TenantAdmin | the second tenant, for the AC-4 checks |
| `approver@globex.test` | Approver | a real, active, Approver-role user who must still be refused cross-tenant |
| `sysadmin@bookspace.local` | SysAdmin | proving the Platform Operator is refused on tenant endpoints |

---

## What each folder proves

**00 Setup — identities.** Logs in as each account and stashes the token. It also
extracts the user id from the JWT's `sub` claim, because **nothing in the API
lists users** — see "Gaps this found" below. Acme's admin logs in last, so the
collection-level token is the admin's.

**01 Resources (FR-3.1, FR-3.5).** Create, read, list with paging and sorting.
Also: `pageSize=500` is *rejected*, not clamped (decision `0015`), and a Windows
timezone id (`Eastern Standard Time`) is refused even though `TimeZoneInfo`
resolves it on Windows (CLAUDE.md §4.3).

**02 Availability windows (FR-3.2).** Replace-the-set. Three windows sent out of
order come back ordered; adjacent windows are accepted; overlapping ones are
**409**; a backwards window is a **400 naming `Windows[0].ClosesAt`**; sub-second
times are rejected rather than rounded; an empty array clears the schedule while a
missing one is a 400. One request re-reads the resource after the 409 to show the
rejected replace wrote nothing.

**03 Approvers (FR-3.3).** Assign the Approver-role user, then a TenantAdmin
(both eligible — decision `0018`). A Member, another tenant's real approver, and a
Guid that exists nowhere are all **422 `ApproverNotEligible`** — and the last two
produce *byte-identical* bodies, which is the AC-4 property the code was named
for. Then the sequence that matters: assign → set `RequiresApproval` → emptying
the list is refused → but swapping approvers in one call works. Finishes by
reading the resource as a **member**, who sees both the schedule and the
approvers.

**04 Acceptance criteria.** Member and Approver both get 403 on every write.
SysAdmin gets 403 on a read. A real Globex resource id gives Acme a 404 that is
byte-identical to a nonexistent id's. Acme cannot write a schedule onto a Globex
room. And `X-Correlation-Id` round-trips into the body, the response header, and
every Serilog line.

**05 Archive and teardown (FR-3.5).** Archive is idempotent and does not move
`updatedAtUtc`; an archived resource refuses new schedules and approvers (422)
but stays readable with everything intact; it disappears from the default list and
comes back with `includeArchived=true`.

---

## Two checks worth doing by eye

The runner proves these, but they are more convincing watched:

1. **Cross-tenant is invisible, not forbidden.** Log in as `member1@acme.test`,
   request a real Globex resource id, and compare the response to one for a
   random Guid. Identical apart from the correlation id. Nothing tells you the
   first id exists.

2. **403 and 422 come from different layers.** `POST /resources` as a member is a
   bare **403** from the authorization policy — no reason code, no handler ran.
   Emptying an approver list on a resource that requires approval is a **422 with
   `reasonCode`** from a handler. Which one you get tells you whether you are
   testing RBAC or a domain rule.

Set an `X-Correlation-Id` header while poking around — it comes back on the
response and tags every log line for that request, which makes the console
readable.

---

## Three behaviours that look like bugs and are not

1. **A SysAdmin token gets 403 on resource endpoints.** The `TenantMember` policy
   requires an `orgId` claim, which decision `0009` deliberately omits for a
   SysAdmin (PRD §2: the Platform Operator must never see tenant booking content
   in routine operation). Use `admin@acme.test`.

2. **Re-sending an already-rotated refresh token kills the whole token family**
   (decision `0011`), so the next call fails with 401
   `RefreshTokenReuseDetected`. That is the feature working; in Postman, where
   re-sending an old request is one click, it reads as a random 401. Log in again.
   Access tokens last 15 minutes.

3. **Availability window ids change on every PUT.** The set is rebuilt rather
   than diffed — `AvailabilityWindow` has no audit columns precisely because
   entries are bulk-replaced. Do not cache a window id. Approver rows are keyed by
   `(ResourceId, UserId)` and *do* stay stable, so the two endpoints differ here.

---

## Cleanup

The collection archives its room rather than deleting it — nothing in this system
is deleted (CLAUDE.md §4.5), and there is no `Unarchive`. Every full run therefore
leaves one more archived `Postman Test Room` behind. They are invisible in the
default list, so this is cosmetic, but to reset the dev database:

```
cd backend/src/BookSpace.Api
dotnet ef database drop -f
dotnet run          # Development re-migrates and re-seeds on startup
```

---

## Gaps this exercise found

Recorded here rather than fixed, because neither is in WP-3's scope (CLAUDE.md
§11):

- **There is no endpoint that lists users.** An admin using only the API cannot
  discover the Guid to put in an approver list — the collection works around it by
  logging in *as* each user and decoding `sub` from the JWT, which a real admin
  obviously cannot do. FR-3.3 is usable from a UI that already has a user
  directory; it is not usable from the API alone. Worth raising before the
  frontend needs it.

- **`GET /openapi/v1.json` returns 401 without a bearer token.** The
  deny-by-default `FallbackPolicy` (decision `0012`) covers `MapOpenApi()` too, so
  importing the spec into Postman needs a token attached like any other request.
  Options: leave it and remember the token step, or `MapOpenApi().AllowAnonymous()`
  in the Development branch only. Opening an endpoint is an authorization
  decision, so it was left alone — the committed collection means you do not need
  the spec import anyway.
