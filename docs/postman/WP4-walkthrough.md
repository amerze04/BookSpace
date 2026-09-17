# Postman — hand walkthrough for WP-4 (the booking engine)

Companion to `README.md`, which covers WP-3. Same style: a hand walkthrough, not
a collection — the committed `BookSpace.postman_collection.json` stops at WP-3
Phase 3 and has no booking requests in it.

The automated suite (**879 unit + 406 integration**) stays the source of truth.
This is for what a client shows better than a test log: response shapes, error
bodies, and demonstrating the acceptance criteria to someone watching.

**One thing here cannot be done by hand at all** — see §9.

---

## Setup

1. Start the API in Development:

   ```
   cd backend/src/BookSpace.Api
   dotnet run
   ```

   Development seeds on startup. **New in WP-4: the seed now contains bookings** —
   three per tenant, so §2 has something to read before you create anything.

2. Base URL `http://localhost:5270` (or `https://localhost:7079` with SSL
   verification off for the dev certificate).

3. Log in and keep the `accessToken`. Every request below needs
   `Authorization: Bearer {{token}}`, and the access token lasts **15 minutes** —
   if requests start returning 401 mid-walkthrough, log in again rather than
   hunting for a bug.

   ```
   POST /auth/login
   { "email": "member1@acme.test", "password": "Passw0rd!" }
   ```

Accounts used here, all with password `Passw0rd!`:

| Email | Role | Used for |
|---|---|---|
| `member1@acme.test` | Member | the booker throughout |
| `approver@acme.test` | Approver | the "other member" (a non-admin), and the approver on the printer |
| `admin@acme.test` | TenantAdmin | creating test resources, blackouts, and the decision-`0002` cancel |
| `admin@globex.test` | TenantAdmin | the AC-4 cross-tenant checks |
| `sysadmin@bookspace.local` | SysAdmin | proving the Platform Operator is refused |

> **Do not use `member2@acme.test`.** The integration suite deactivates it
> permanently by design, and if you have run the tests against this database its
> **login** will fail with a 401 that looks like a bug and is not.

### Resource ids you will need

```
GET /resources
```

as `member1@acme.test`. Note the ids of **Conference Room A** (capacity 1, no
approval, 30–240 min) and **3D Printer** (capacity 1, **requires approval**,
60–180 min). Both open **09:00–17:00 local, Monday–Friday**, in
`America/New_York` — so pick UTC instants that land inside that. **14:00–15:00
UTC works year round** for Acme.

---

## 1. The happy path (FR-4.1)

Pick a **weekday** a few days out, and a slot inside the window.

```
POST /bookings
{
  "resourceId": "<Conference Room A>",
  "startsAtUtc": "2027-04-08T14:00:00Z",
  "endsAtUtc":   "2027-04-08T15:00:00Z",
  "quantity": 1,
  "title": "Design review"
}
```

Expect **201 Created**, with a `Location` header, and in the body:

- `status: "Confirmed"` — **as a name, not a number**. Enums serialize as their
  names app-wide since WP-3.
- instants ending in **`Z`**. If a `Z` is ever missing, that is a real bug
  (CLAUDE.md §4.3) — a browser would read it as local time.
- `approval: null` — nothing to approve on this resource.

Keep this booking's `id`.

`quantity` and `title` are both optional; omitting `quantity` binds to **1**, not
0. There is deliberately **no `userId` field** — a member books for themselves
and the actor comes from the token, so it cannot be forged by editing the body.

## 2. Reading them back (FR-4.4)

```
GET /bookings
```

You see **your own** bookings — the one you just made plus the seeded ones owned
by `member1`. Try the filters:

```
GET /bookings?resourceId=<Conference Room A>
GET /bookings?status=Confirmed
GET /bookings?from=2027-01-01T00:00:00Z&to=2028-01-01T00:00:00Z
GET /bookings?page=1&pageSize=2&sort=-startsAtUtc
GET /bookings/<id>
```

Things worth pointing at:

- **`from`/`to` are overlap, not containment** — a booking that starts before
  `from` and runs into the range is included.
- **`sort`** accepts only `startsAtUtc`, `createdAtUtc`, `status`, each
  optionally prefixed `-` for descending. Anything else is **400**, and
  `pageSize=101` is **400 rather than clamped** (decision `0015`).
- An **unknown `resourceId` is an empty page, not a 404** — deliberately unlike
  the blackout list, where the resource is in the route.

### The member/admin split (decision 0002)

As `member1`, both of these are **400 `ValidationFailed`** naming the field —
not a quietly narrowed 200:

```
GET /bookings?scope=Tenant
GET /bookings?userId=<any guid>
```

As `admin@acme.test`, both work — and note that **an admin still sees only their
own by default**. They have to ask:

```
GET /bookings?scope=Tenant
```

## 3. Approval routing (FR-7.1)

Book the **3D Printer**, respecting its 60–180 minute limits:

```
POST /bookings
{
  "resourceId": "<3D Printer>",
  "startsAtUtc": "2027-04-08T14:00:00Z",
  "endsAtUtc":   "2027-04-08T16:00:00Z"
}
```

Expect **201** with `status: "Pending"` and a populated **`approval`** object
(its id, and `expiresAtUtc` 24 hours out — Acme's `ApprovalExpiryHours`).

The important part: it was **not refused**. FR-7.1 makes an approval-gated
booking wait rather than fail, which is why the `ApprovalRequired` reason code
was **deleted** in WP-4 — nothing can ever throw it.

**Nothing can approve it yet.** `dbo.ApproveBooking` and the approve/reject
endpoints are WP-5. WP-4 creates the row those will decide.

## 4. Every rejection reason (FR-4.3, FR-4.5)

Each of these returns a `ProblemDetails` body carrying a `reasonCode` and a
`correlationId`. **The exception message never appears** — `detail` is generic
per kind, by design (decision `0016`).

| Try this | Expect |
|---|---|
| a `resourceId` that exists nowhere | **404** `ResourceNotFound` |
| a **Globex** resource id (get one as `admin@globex.test`) | **404** `ResourceNotFound` — *byte-identical* to the line above (AC-4) |
| `03:00Z–04:00Z` (before the room opens in New York) | **422** `OutsideAvailability` |
| a slot inside a blackout — create one first, §5 | **422** `BlackoutPeriod` |
| `14:00Z–14:10Z` on the room (min 30 min) | **422** `BookingDurationOutOfRange` |
| any slot last week | **422** `BookingInThePast` |
| §1's exact slot again, as `approver@acme.test` | **409** `SlotUnavailable` |
| `quantity: 0`, or `endsAtUtc` before `startsAtUtc` | **400** `ValidationFailed` with per-field `errors` |

Two worth understanding rather than just ticking:

- **`BookingInThePast` tests the *end*, not the start.** A slot that started an
  hour ago and runs for another hour is **accepted** — "book the room I am
  already sitting in" is the ordinary case.
- **`SlotUnavailable` vs `CapacityExceeded` is decided by what is left at the
  peak**, and nothing else: `dbo.CreateBooking` answers `SlotUnavailable` when
  the remaining capacity is **zero or less**, and `CapacityExceeded` whenever
  something is left but less than was asked for.
  **Corrected 2026-09-17.** This bullet used to say `CapacityExceeded` "cannot
  be produced on either seeded resource, because both are capacity 1", and that
  asking for 5 up front on a capacity-4 resource gives `SlotUnavailable`. Both
  are wrong, and the procedure's own `CASE WHEN @remaining <= 0` is why:
  - `quantity: 2` on a free, capacity-1 seeded resource leaves 1 remaining, so
    it is **409 `CapacityExceeded`** — verified against the running API. The
    validator deliberately leaves `Quantity` unbounded (it cannot see
    `Capacity`), so the request is well-formed and reaches the procedure.
  - `quantity: 5` on a free, capacity-4 resource leaves 4 remaining, so it is
    **`CapacityExceeded`** as well, not `SlotUnavailable`.
  - To actually see `SlotUnavailable`, the slot has to be **fully taken**: book
    `quantity: 4` on that capacity-4 resource first, then ask for any quantity
    on the same slot.

## 5. Blackouts beat bookings (FR-3.4, decision 0001)

As `admin@acme.test`, black out a span covering §1's booking:

```
POST /resources/<Conference Room A>/blackout-periods
{
  "startsAtUtc": "2027-04-08T13:00:00Z",
  "endsAtUtc":   "2027-04-08T18:00:00Z",
  "reason": "Floor resurfacing"
}
```

**This is the first time you can see the cascade from a client.** Until WP-4
there were no bookings to cancel, so `cancelledBookings` was always empty in
Postman. Now the response lists §1's booking as cancelled. Confirm it as
`member1`:

```
GET /bookings/<id>     → status "Cancelled", with cancelledAtUtc and a reason
```

Then try to book inside the blackout → **422 `BlackoutPeriod`**.

`DELETE` the blackout afterwards. Note it **un-cancels nothing** (decision
`0019`) — the cancellation is irreversible, so the slot is free again but the old
booking stays cancelled.

## 6. Cancelling (FR-4.4, AC "the slot is freed")

Make a fresh booking, then:

```
POST /bookings/<id>/cancel
{ "reason": "No longer needed" }
```

**200 OK**, with `status: "Cancelled"`, `cancelledByUserId`, `cancelledAtUtc`,
and the freed interval echoed back. A body is optional — `POST` with **no body
at all** must also work, giving a null reason.

Then prove the slot is genuinely free, which is the acceptance criterion:

```
GET /resources/<id>/availability?from=2027-04-08&to=2027-04-08
```

The interval is offered again — and re-booking it as `approver@acme.test` now
**succeeds** where it was 409 before.

Four behaviours to try, each deliberate:

| Try | Expect | Why |
|---|---|---|
| cancel the **same booking twice** | **422** `BookingNotCancellable` | unlike archiving a resource, there is an actor and a reason to overwrite |
| cancel **someone else's** booking as `member1` | **404** `BookingNotFound`, never 403 | a 403 would confirm the id exists (AC-4) |
| cancel a **Globex** booking as `admin@acme.test` | **404**, byte-identical | an admin's reach stops at their tenant |
| cancel **another member's** booking as `admin@acme.test` | **200** | decision `0002` — and the owner gets a `Notifications` row, while cancelling your *own* enqueues none |

The row is never deleted (§4.5) — `GET /bookings/{id}` still returns it.

## 7. Authorization

| Request | Expect |
|---|---|
| any of the above with no `Authorization` header | **401** |
| `POST /bookings` as `sysadmin@bookspace.local` | **403**, no reason code — `TenantMember` requires the `orgId` claim a SysAdmin does not have (decision `0012`) |
| `POST /bookings` as Member, Approver **and** TenantAdmin | **all 201** — booking is not admin-only |

A **403 has no `reasonCode`** and a rule violation does. Which one you get tells
you whether you hit RBAC or a domain rule.

## 8. Tenant isolation (AC-4)

As `admin@globex.test`, create a booking on a Globex resource and note its id.
Then as `member1@acme.test`:

```
GET /bookings/<globex booking id>              → 404
POST /bookings/<globex booking id>/cancel      → 404
GET /bookings?scope=Tenant                     → 400 (member), and as
                                                  admin@acme.test: only Acme rows
```

Every one is a **404 or an absent row, never a 403** — the isolation is a `WHERE`
clause, so the row is never fetched in the first place.

## 9. What this walkthrough cannot show

**AC-1, the concurrency guarantee — the whole point of WP-4.** Postman fires
requests one at a time, so nothing here contends for the lock. Two simultaneous
requests for one slot is proved in the automated suite at two levels
(`CreateBookingProcedureTests` and `BookingConcurrencyEndpointTests`), and the
measured evidence is in
`docs/decisions/0023-booking-concurrency-strategy.md` — including the figures
from removing the lock hints: **ten** confirmed bookings on a room that holds
one.

If you want to *show* it live, Postman's Collection Runner will not do it (it is
sequential). The closest hand demo is `docs/wp4-defense.md` §8's table plus
running the concurrency test file with output on:

```
cd backend
dotnet test tests/BookSpace.IntegrationTests --filter "FullyQualifiedName~BookingConcurrencyEndpointTests"
```

Also not visible from a client: the `Notifications` rows every path writes
(nothing dispatches them yet — CLAUDE.md §7), and the approve/reject decision on
a `Pending` booking (WP-5).

---

## Three things that look like bugs and are not

1. **A `Pending` booking still consumes capacity.** Book the printer, then try
   the same slot again → 409, even though nobody has approved anything.
   Deliberate: `Pending` and `Confirmed` are the two statuses that hold a claim
   (decision `0005`), so a request cannot be double-promised while it waits.
2. **A cancelled `Pending` booking keeps its approval request at `Pending`.**
   Nothing withdraws it, and there is no endpoint to see it yet. A known,
   documented loose end that **WP-5 must handle** — its approve path has to check
   the booking's status or an approver could approve a cancelled booking.
3. **A booking that has already ended cannot be cancelled**, but one *in
   progress* can. The window is `EndsAtUtc`, not `StartsAtUtc`. This matters
   because **nothing in the system writes `Completed`** — an attended meeting is
   still `Confirmed`, so status alone would let a member rewrite history.

---

## Cleanup

Bookings are never deleted by any endpoint (§4.5), so a walkthrough leaves rows
behind. That is harmless — but if you want a clean dataset, drop and re-seed:

```
cd backend/src/BookSpace.Api
dotnet ef database drop -f
dotnet run
```

Anything you created on a **test resource** goes away with it if you archive the
resource — but archiving does not delete either, so a real reset is the drop
above.
