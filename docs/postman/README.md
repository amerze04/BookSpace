# Postman — manual verification for WP-3

Two files, both committed:

- `BookSpace.postman_collection.json` — 38 requests in five folders (`00`–`05`),
  every one carrying assertions. **Covers Phases 1–3 only** — it has no blackout
  folder; Phase 4 was verified by the hand walkthrough in §`06` below.
- `BookSpace.postman_environment.json` — base URL and the seeded accounts. No
  secrets: the only password in it is `SeedData.SeedPassword`, which exists in
  development only.

The automated suite (529 unit + 226 integration) stays the source of truth. This
is for the things a client shows better than a test log: response shapes, error
bodies, and demonstrating the acceptance criteria to someone watching.

**Status (2026-09-02):** both walkthroughs below — Phases 1–3 (§`00`–`05`) and
Phase 4's blackout periods (§`06`) — were executed by hand against a running
instance by the repo owner, and everything behaved as documented. The
**collection JSON has not itself been run**, and does not yet include Phase 4 —
it was written from the code, not from a green runner pass. Treat a first
Collection Runner run as debugging the collection, not as testing the API; if a
request fails there, suspect the script before the endpoint.

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

## 06 Blackout periods (FR-3.4) — hand walkthrough, not in the collection

Verified by the repo owner on 2026-09-02, immediately after WP-3 Phase 4 landed.
**No requests for this exist in the committed collection yet**, so this section is
the record of what was run, in order, and is what to repeat.

Two setup notes that are easy to get wrong:

- **Never run the archived-resource checks against a resource you still need.**
  Archiving is irreversible (§4.5, no `Unarchive`), so those steps use a
  throwaway resource created for the purpose.
- **Every timestamp needs a trailing `Z` and whole seconds.** Both rules are
  deliberate and both are tested below — a bare local-looking timestamp is a 400,
  and so is a fractional second.

The walkthrough, with what each step demonstrates:

1. **Create** → **201**, `Location` pointing at the list, and
   `cancelledBookings: []` — empty rather than absent, so a client can tell the
   cascade ran and found nothing.
2. **List** → **200**, the paged envelope from decision `0015`. Note the seeded
   "Public holiday" blackout on 2026-12-25 already sits on Acme's open resource.
3. **Create a second, overlapping blackout** → **201**. Deliberately unlike
   availability windows, which reject overlap with a 409: the union of two
   blackouts is still blacked out, so there is nothing to disambiguate (decision
   `0019`).
4. **`?from=…&to=…`** → returns a blackout that *started before* the window and
   runs into it, and drops one that ended before it. Overlap, not containment.
5. **`?sort=-startsAtUtc`** reverses the order; **`?sort=reason`** is a **400**,
   since it is not on the whitelist.
6. **The list as a member** → **200**. On `TenantMember`, not `TenantAdmin`: a
   member choosing when to book needs to see when the room is blocked.
7. **A timestamp with no zone** → **400** naming `StartsAtUtc`. The §4.3 guard —
   the only available interpretation would be the *server's* timezone.
8. **Fractional seconds** → **400**. `datetime2(0)` rounds, so the response would
   disagree with the row.
9. **An inverted interval** → **400** on `EndsAtUtc`.
10. **A blackout entirely in the past** → **422 `BlackoutPeriodElapsed`**.
11. **A blackout that merely *starts* in the past** → **201**. The contrast that
    matters: "the room flooded this morning and is unusable until Friday" is the
    ordinary operational case, and a `StartsAtUtc >= now` rule would also lose to
    clock skew.
12. **A resource id that exists nowhere** → **404 `ResourceNotFound`**.
13. **`PUT`** → **200**, `updatedAtUtc` moved and `createdAtUtc` did not, and the
    list length is unchanged — an edit, not a second row.
14. **`PUT` omitting `reason`** → **200** with `reason: null`. A full
    representation, so an omitted field means *cleared*, not *unchanged*
    (decision `0015`).
15. **The two 404s, side by side** — an unknown blackout id gives
    **`BlackoutPeriodNotFound`**, while a *real* blackout id reached through a
    Globex resource gives **`ResourceNotFound`**, because the resource is checked
    first and is already invisible. Distinct codes because they say different
    things about the same URL: which half of the path is wrong.
16. **`PUT` moving a blackout entirely into the past** → **422**, and the list
    shows it **unchanged** — every rule runs before any mutation.
17. **On a throwaway archived resource**: `POST`, `PUT` and `DELETE` all →
    **422 `ResourceArchived`**. Including the delete, which is decision `0019`'s
    deliberate call: "an archived resource accepts no writes" is a rule an admin
    can hold in their head, and the alternative makes deletion its one exception.
18. **`DELETE`** → **204** with an empty body, and the row is *gone* from the
    list rather than flagged. The first real hard delete in this system.
19. **`DELETE` the same id again** → **404 `BlackoutPeriodNotFound`**,
    deliberately not 204: it cannot distinguish "already deleted" from "another
    tenant's id", so it accepts neither.
20. **`POST`/`PUT`/`DELETE` as `member1@acme.test`, `approver@acme.test` and
    `sysadmin@bookspace.local`** → **403** every time, with **no `reasonCode`** —
    the authorization policy refused it before any handler ran.

### What this walkthrough cannot show

**The cascade itself.** Every `cancelledBookings` array comes back empty, because
there is no way to create a booking yet — `dbo.CreateBooking` is WP-4 (§4.1). So
the one behaviour that makes this phase interesting is the one Postman cannot
reach.

Decision `0001`'s cascade is covered by `BlackoutPeriodEndpointTests`, which
inserts booking rows with raw SQL under decision `0017`'s carve-out and then
asserts against `dbo.Bookings` and `dbo.Notifications` directly. Seeing it by
hand needs the same raw insert against the dev database, with an explicit RLS
bypass or the statement's own `SELECT` silently matches zero rows. Worth
revisiting from Postman once WP-4 gives bookings a real write path — at that
point this section and the collection should both grow a cascade folder.

---

## 07 The availability query (WP-3 Phase 5) — hand walkthrough, not in the collection

Written 2026-09-03 alongside the endpoint; **not yet run by the owner**, and no
requests for it exist in the committed collection. This section is the
walkthrough to follow.

One thing to know before starting, or the first response looks broken: **the
`from` and `to` parameters are resource-local dates, not instants** — `from=2026-09-07`,
not a timestamp — while the intervals that come back are **UTC instants**. That is
deliberate (decision [`0003`](../decisions/0003-availability-timezone.md) makes
availability resource-local; a bookable span has to be unambiguous, and on a
clocks-back day a local time names two instants). The response echoes
`timeZoneId` and both dates so you can see how the server read them.

Setup — a resource whose local time *is* UTC makes every response readable
without doing offset arithmetic in your head:

1. `POST /resources` as `admin@acme.test` with `"timeZoneId": "UTC"`,
   `"capacity": 4`, `"minDurationMinutes": null`. Keep the id.
2. `PUT /resources/{id}/availability-windows` with a single Monday window,
   `09:00:00`–`17:00:00`.

Then, in order:

| # | Request | What it should show |
|---|---|---|
| 1 | `GET /resources/{id}/availability?from=2026-09-07&to=2026-09-07` | One interval, `09:00Z`–`17:00Z` (the resource is in UTC, so local times are instants), `remainingCapacity: 4`, `isArchived: false`. |
| 2 | Same, `from=2026-09-08&to=2026-09-08` (a Tuesday) | `intervals: []`. Nothing is wrong — the schedule has no Tuesday window. |
| 3 | `PUT` the windows again as **two adjacent** Monday windows, `09:00–12:00` and `12:00–17:00`, then repeat request 1 | Still **one** interval, `09:00Z`–`17:00Z`. Adjacent windows are legal (Phase 3) and describe one span. |
| 4 | `PUT` the windows as Monday `22:00:00`–`23:59:59` **plus** Tuesday `00:00:00`–`02:00:00`, then `?from=2026-09-07&to=2026-09-08` | One interval, Monday `22:00Z` → Tuesday `02:00Z`. The overnight span, rejoined — `23:59:59` is read as midnight (decision [`0022`](../decisions/0022-availability-window-midnight-convention.md)). |
| 5 | Restore the single Monday `09:00–17:00` window. `POST /resources/{id}/blackout-periods` covering `12:00Z`–`13:00Z` on the **next** Monday, then query that Monday | **Two** intervals, `09:00–12:00` and `13:00–17:00`. The blackout removed the time (FR-3.4). Use a future Monday: a blackout entirely in the past is refused. |
| 6 | `POST` a second, overlapping blackout `12:30Z`–`14:00Z`, then query again | `09:00–12:00` and `14:00–17:00`. Overlapping blackouts are allowed (decision `0019`) and their **union** is what disappears. |
| 7 | `DELETE` both blackouts, then query again | Back to one interval. Deleting a blackout restores availability — it un-cancels no bookings, but the time is bookable again. |
| 8 | `GET .../availability?from=2026-09-07&to=2026-12-07` (92 days) | **400** `ValidationFailed`, with a field error naming 90 days. Rejected, not clamped. |
| 9 | Same with `to` **before** `from` | **400** `ValidationFailed`. An empty list would be indistinguishable from a closed resource. |
| 10 | Omit both parameters entirely | **400** `ValidationFailed`, `FromLocalDate is required.` |
| 11 | `from=yesterday` | **400** from model binding, *without* a `reasonCode` — a malformed date never reaches the validator. Correct, and worth seeing once so the difference is familiar. |
| 12 | Query as `member1@acme.test` | **200.** This is the endpoint the member flow runs on; it is not admin-only. |
| 13 | Query as `sysadmin@bookspace.local` | **403.** `TenantMember` requires the `orgId` claim, which decision `0012` omits for a SysAdmin. |
| 14 | Query a **real** Globex resource id as `member1@acme.test` | **404** `ResourceNotFound`, byte-identical to a random Guid (AC-4). |
| 15 | `POST /resources/{id}/archive`, then query | **200**, `intervals: []`, `isArchived: true`. Not a 422 — FR-3.5 keeps an archived resource readable and "nothing is bookable" is the true answer. Use a throwaway resource: archiving is irreversible. |

Two more worth doing on a **second** resource created with
`"timeZoneId": "America/New_York"` and a single all-day window
(`00:00:00`–`23:59:59`) on a Sunday, because they are the ones a UTC resource
cannot show:

| # | Request | What it should show |
|---|---|---|
| 16 | `?from=2026-03-08&to=2026-03-08` | One interval lasting **23 hours** — the clocks went forward. |
| 17 | `?from=2026-11-01&to=2026-11-01` | One interval lasting **25 hours** — the clocks went back. Decision [`0021`](../decisions/0021-daylight-saving-for-availability-ranges.md). |

### What this walkthrough cannot show

**`remainingCapacity` below the resource's full capacity.** Every interval above
comes back with all units free, because consuming capacity needs a booking and
there is no booking write path until WP-4 (§4.1, decision `0017`). The partial
cases — one unit of four taken leaving three, concurrent bookings summing, and
time disappearing only once the units run out — are covered by
`AvailabilityEndpointTests`, which inserts bookings by raw SQL. Worth repeating
from a client once WP-4 lands, together with the blackout cascade for the same
reason.

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
   **Blackout ids are stable too** — a blackout is an individual event with its
   own audit columns, edited in place by `PUT`, so it behaves like neither of the
   replace-the-set endpoints.

4. **`cancelledBookings` is always empty.** Not a broken cascade — there is no
   booking write path until WP-4, so there is nothing for a blackout to cancel.
   See the end of §`06`.

5. **A second `DELETE` of the same blackout is a 404, not a 204.** DELETE is
   idempotent in the sense that the end state matches, and 204 would be
   defensible — but this endpoint cannot tell "already deleted" from "another
   tenant's id" (AC-4), so it refuses both. Contrast
   `POST /resources/{id}/archive`, which *is* idempotent, because there the row
   is still present to inspect.

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
