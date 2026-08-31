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
