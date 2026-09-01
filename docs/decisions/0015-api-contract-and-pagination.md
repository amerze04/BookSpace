# 0015 — API contract conventions: DTOs, pagination, sorting

**Status:** Decided (2026-08-31). The pagination scheme was chosen by the repo
owner; the DTO conventions formalize what WP-2 already did implicitly.
**Requirements:** WP-3 task 6 ("design clean DTOs, error contracts, and
pagination") and its AC "the API returns clear, structured errors".
**Raised by:** WP-3 Phase 1, before the first list endpoint existed.

## Context

WP-3 adds the API's first list endpoints and its first DTOs outside
authentication. Nothing in the codebase paginates yet, and the DTO convention
so far is implicit — inferable from `AuthController`/`AuthenticationResult`, but
written down nowhere. Establishing both before Phase 2 is the same reasoning
the mentor accepted for building the mediator before login in WP-2: a contract
retrofitted across endpoints that already exist is how the inconsistency the AC
forbids gets in.

## Decision

### 1. Offset pagination, with a total count

`?page=&pageSize=&sort=` in, `PagedResult<T>` out:

```json
{ "items": [ … ], "page": 2, "pageSize": 20, "totalCount": 47,
  "totalPages": 3, "hasPreviousPage": true, "hasNextPage": true }
```

`totalPages`/`hasPreviousPage`/`hasNextPage` are computed from the other three
rather than stored — three copies of a derivable fact is three ways to
disagree. Zero rows is zero pages, not one empty page.

**Rejected: offset without a total count** (fetch `pageSize + 1`, report only
`hasMore`). It saves the second `COUNT(*)` per request, but the client can then
never show "page 3 of 5" or a result total, and the Angular app at M4 wants
both. Note the upgrade path runs the other way too: adding a count field later
breaks no client.

**Rejected: keyset/cursor paging.** Offset paging has two real weaknesses —
rows can repeat or be skipped when data changes between page requests (an
insert before your position shifts every later row down one), and `OFFSET
100000` makes the server walk and discard 100,000 rows. Keyset has neither, but
costs page numbers, totals, and jump-to-page, and needs a unique tiebreaker
encoded in every cursor. Neither weakness bites here: a tenant has tens of
resources, page 5000 does not exist, and a duplicated row during an admin's
edit session is cosmetic — the correctness guarantee this project actually
cares about (zero double-bookings, AC-1) lives in the booking path, nowhere
near a list endpoint. Revisit if an endpoint ever pages over `Bookings`, where
row counts genuinely grow.

Note the availability query (Phase 5) is **not** paginated at all: it is a
function of a date range, bounded by a maximum span, not a list of records.

### 2. Page size: default 20, maximum 100, rejected not clamped

An oversized `pageSize` is a validation failure with a message, not a silently
clamped request. Clamping hides the bug from whoever wrote the client, and
"why did I only get 100 rows" is a worse debugging session than a 400.

### 3. Sorting is part of paging, not decoration

Offset paging is only correct over a total order: without `ORDER BY`, SQL
Server may return rows in any order, so "rows 21–40" is undefined and pages can
overlap or skip — and an `ORDER BY` with ties has the same problem among the
tied rows. So:

- Every paged query applies an `ORDER BY` **ending in a unique column**
  (normally `Id`) before paging.
- `PagedQueryableExtensions.ToPagedResultAsync` throws if the query has no
  ordering at all. It cannot check for the unique tiebreaker, which stays the
  query author's responsibility, but the common mistake — forgetting to order
  entirely — now fails loudly instead of producing quietly wrong pages.
- Wire syntax is `sort=name` ascending, `sort=-name` descending. Omitted means
  "the endpoint's own default order", not an error.
- Each endpoint passes an explicit **whitelist** of sortable fields.
  `SortOption.TryParse` matches case-insensitively and returns the whitelist's
  spelling, so handlers switch on a canonical name. A `sort` value never
  reaches a LINQ expression as a string — each query maps the canonical name
  onto a typed `OrderBy` itself — so the whitelist is a contract check that
  keeps the sortable surface deliberate, not an injection guard.

### 4. DTO conventions

Formalizing WP-2's implicit pattern rather than replacing it:

- **HTTP request shapes are records nested in the controller** that owns them
  (`AuthController.LoginRequest`). They describe the wire, so they live with
  the wire.
- **Commands, queries and response DTOs live in
  `Application/Features/<Feature>/<UseCase>/`** next to the handler that
  produces them (`AuthenticationResult`). The controller maps its request onto
  the command; the two are separate types even when their fields coincide, so
  an HTTP shape can change without touching a handler's contract.
- **Records, not classes**, and `sealed`.
- **Domain entities are never on the wire**, in either direction. Every
  response is a DTO, so no `IsArchived` flag or audit column leaks by accident
  when an entity gains a property.
- **Mapping is hand-written**, in the handler or a small static mapper. No
  AutoMapper: this is an internship project and every mapping should be
  readable and defensible without knowing a library's conventions.
- **Controllers only map and dispatch** through `ISender` (WP-2's rule,
  unchanged).
- **Validation is FluentValidation, typed against the concrete request type.**
  `IValidator<T>` is invariant, so a validator generic over a base or interface
  is never discovered — see CLAUDE.md §12's gotcha. Shared rules are supplied
  as extension methods (`PagedQueryRules.AddPagingRules`), not a base
  validator class, so a query's single inheritance slot isn't spent on paging.
- **Edits are full-representation `PUT`**: every mutable field is supplied, and
  an absent field means null/cleared rather than "leave unchanged". That
  sidesteps the "not supplied vs. explicitly null" problem entirely for WP-3.
  If a genuine partial update (`PATCH`) is ever needed, it gets an explicit
  optional wrapper type then, and this convention is what it would deviate
  from.

## Consequences

- New in `BookSpace.Application/Common/Pagination/`: `PagedResult<T>`,
  `IPagedQuery`, `PagingDefaults`, `SortOption`, `PagedQueryRules`.
- `ToPagedResultAsync` lives in `BookSpace.Infrastructure.Persistence`, not
  beside `PagedResult`: `CountAsync`/`ToListAsync` are EF Core, and
  `BookSpace.Application` deliberately has no EF dependency (CLAUDE.md §3).
- Both queries it runs go through the caller's `DbContext`, so the global query
  filters and RLS apply to the count as well as the page — a total can never
  advertise rows the page itself would hide. There is a test for exactly that.
- **Nothing consumes any of this yet.** Phase 2's `GET /resources` is the first
  caller. Until then it is covered by unit tests only, which is the accepted
  cost of building the contract before the endpoints.

## Notes

The `sort` whitelist and the ordering guard are the two pieces most likely to
feel like overhead when writing the first list endpoint. They are here because
the failure they prevent — wrong rows on page 2, or an unsortable field
silently ignored — is invisible in a green test suite that only ever asks for
page 1.

---

## Amendment (2026-09-01) — request naming, and one response DTO per endpoint

**Status:** Amended and implemented (2026-09-01). The pagination scheme,
sorting rules and the rest of §4's DTO conventions are unchanged; the two items
below replace what §4 said about naming and about sharing.
**Raised by:** the mentor, reviewing the WP-3 Phase 2 code.

### 1. Everything implementing `IRequest<T>` is named `…CommandRequest` / `…QueryRequest`

`LoginCommand` → `LoginCommandRequest`, `GetResourceQuery` →
`GetResourceQueryRequest`, and so on. Handlers and validators follow the type
they serve (`LoginCommandRequestHandler`, `LoginCommandRequestValidator`), so the
trio of files in a use-case folder keeps lining up; leaving a
`LoginCommandHandler` beside a `LoginCommandRequest` would name a handler after a
type that no longer exists.

**The cost, recorded honestly:** "command" already means "a request that changes
something", so `CommandRequest` is redundant on its face, and the API project now
holds `CreateResourceRequest` (the HTTP wire record, nested in the controller)
next to `CreateResourceCommandRequest` (the mediator message). Those two names are
one word apart and describe different layers.

What keeps them apart is a rule that was already true and is now load-bearing, so
it is written down here: **a request type nested inside a controller is the wire
shape; a request type in `Application/Features/…` is the mediator message.**
Nothing else declares HTTP request records, and nothing else declares
`IRequest<T>`.

### 2. Response DTOs are per-endpoint, in their own files, never shared

§4 said commands and response DTOs live beside their handler. It did not say
whether two endpoints may return the same response type, and in practice they
did: `AuthenticationResult` served both `/auth/login` and `/auth/refresh`, and
`ResourceDetailResponse` served `GET /resources/{id}`, the `POST /resources` 201
body, the archive response, and — nested — the `PUT` response. Four endpoints, one
shape.

**Decided: each endpoint declares its own response record, in its own file, even
when the fields are currently identical.**

The reasoning is the same one §4 already gives for keeping the HTTP request
record separate from the command — "an HTTP shape can change without touching a
handler's contract" — applied one level further. A shared response means a change
for one consumer is a change for all of them.

The concrete case is one phase away. Phase 3 adds availability windows and the
approver list, and those belong on the **read detail**. With a shared type they
would silently appear in the create 201 body and the archive response too, where
a client has no use for a window list it has not created yet — and the archive
response arguably wants *less* than the read detail, not more. Sharing would
force one shape on four endpoints whose needs are already diverging.

**Rejected: sharing until the shapes actually diverge, then splitting.** That is
the cheaper path today and it is what we had. The problem is that the split lands
exactly when the shapes are under pressure — mid-phase, with four call sites and
a Postman collection depending on the old shape — instead of now, with eight
endpoints and no external client. The duplication is the price, and it is visible
on purpose in `ResourceMapping`, where three near-identical mappers sit together.

**Not covered by this rule:**

- **`PagedResult<T>`** stays shared. It is the pagination envelope, and §1's whole
  point is that a client learns it once for every list endpoint. It carries no
  domain fields, so nothing about one endpoint's needs can pull it in a direction
  another does not want.
- **`IssuedTokens`** (formerly `AuthenticationResult`) is not a response DTO at
  all. It is `TokenIssuer`'s return value — an internal collaborator shared by
  login and rotation *by design*, since FR-2.1 and FR-2.2 must mint tokens
  identically. Each handler maps it onto its own response. Splitting the response
  types made that distinction obvious; previously the issuer's output and the
  endpoints' contract were the same type by accident.
- **`ListResourcesQueryResponse`** is the *item* type inside
  `PagedResult<T>`, not a whole response. It was already used by exactly one
  endpoint.

### The one observable wire change

`PUT /resources/{id}` previously returned `{ resource: { … }, timeZoneChange }` —
a wrapper around the shared read-detail DTO. Once the endpoint owns its own
response type, the wrapper buys nothing but a level of nesting, so the shape is
now flat: `{ …fields, timeZoneChange }`. Everything else is byte-identical, which
is why the 142 integration tests needed no changes beyond this endpoint's own
assertions.

### What this cost

Eight request types renamed (with their handlers and validators), two shared
response DTOs split into six, `TimeZoneChangeNotice` moved to its own file, and
`ResourceMapping` grown from one method to three. In the tests, the three
cross-endpoint `Assert.Equal(created, fetched)` record comparisons stopped
compiling — which was the refactor doing its job: "the create body equals the read
body" was an assumption inherited from type sharing, never a property anyone had
stated. They are now `ResourceResponseAssertions.AssertSameResource`, which names
the fields the two endpoints are contracted to agree on.
