# 0016 — One error contract: AppException, ErrorKind, and reason codes

**Status:** Decided and implemented (2026-08-31) — mechanism and catalogue
both. WP-4 adds throwers for the four booking codes, not new plumbing.
**Requirements:** CLAUDE.md §6 ("rejections return a machine-readable reason
code, not just a message"), WP-3's AC "the API returns clear, structured
errors".
**Raised by:** WP-3 Phase 1, item 3.

## Context

WP-2 built `GlobalExceptionHandler` as a deliberate safety net rather than the
full acceptance criterion, and left a comment saying so: three hand-written
cases (`AuthenticationException` → 401, `DbUpdateConcurrencyException` → 409,
`FluentValidation.ValidationException` → 400, plus
`TenantIsolationViolationException` → 500) and a note to extend the switch when
booking rejections arrived.

Extending it literally would mean one exception type and one switch case per
failure. WP-3 alone adds several (resource not found, invalid timezone,
overlapping window, approvers required, capacity below existing bookings), and
WP-4 adds six more from §6's booking list. That is a switch that grows forever,
in the API project, for reasons that belong to the Application layer.

## Decision

**One exception type carrying a *kind*, and one place that translates kind to
HTTP status.**

- `AppException(ErrorKind kind, string reasonCode, string message)` in
  `BookSpace.Application.Common.Errors` is what a handler throws to reject a
  request.
- `ErrorKind` has five members — `Validation`, `Unauthorized`, `NotFound`,
  `Conflict`, `RuleViolation` — each with a real caller today. It names domain
  outcomes, not HTTP: `BookSpace.Application` has no business knowing status
  codes (CLAUDE.md §3).
- `GlobalExceptionHandler` maps kind → status once:

  | `ErrorKind` | Status | Meaning |
  |---|---|---|
  | `Validation` | 400 | A syntactically fine value that isn't acceptable (an unrecognized IANA timezone id) |
  | `Unauthorized` | 401 | Credentials or tokens |
  | `NotFound` | 404 | Doesn't exist, or is another tenant's — indistinguishable on purpose |
  | `Conflict` | 409 | Contradicts current state; a duplicate, or something already changed |
  | `RuleViolation` | 422 | Well-formed and understood, refused by a rule (§6 tier 4) |

- A new failure therefore needs **a reason code and a kind**, not a new case in
  the API project. WP-4's `SlotUnavailable`/`CapacityExceeded`/… arrive with no
  further plumbing, which is exactly what the WP-2 comment asked for.

`AuthenticationException` now derives from `AppException` with
`Kind = Unauthorized`, so authentication maps through the same mechanism as
everything else. It stays a named type rather than becoming a bare
`new AppException(ErrorKind.Unauthorized, …)`: the authentication handlers throw
it in a dozen places, and the type name is what makes FR-2.1's "every credential
failure looks identical to the client" greppable. Its public constructor is
unchanged, so no call site moved.

### Two calls worth defending

**422 for `RuleViolation`, not a second 400.** The request was well-formed and
understood, and a rule refused it — which is what 422 means, and it lets a
client distinguish "you sent nonsense" from "the booking rules said no" without
parsing the reason code. The cost: 422 is less universally recognized than 400,
and some HTTP clients treat anything non-400 as unexpected. The reason code
carries the real meaning either way, so this is a readability preference, not a
load-bearing choice — a mentor who prefers 400 everywhere loses nothing but the
distinction.

**The exception message never reaches the response.** Only `Title` (generic per
kind) and `reasonCode` cross the wire; `Message` goes to the log with the
correlation ID. Nothing in `AppException` decides what is safe to disclose, so
it assumes nothing is. This is load-bearing for authentication — its messages
distinguish "no such account" from "wrong password", which FR-2.1 requires the
client never learn — and it means a `NotFound` for another tenant's id cannot
accidentally confirm that the id exists somewhere. If a per-error client-facing
detail is wanted later, it is an additive field, not a redesign.

An unmapped `ErrorKind` (one added to the enum with no row in the table)
produces 500 rather than a guessed status: a wrong status code is a lie the
client acts on.

## Consequences

- `FluentValidation.ValidationException` keeps its own case rather than being
  folded in. It carries per-field errors (`errors` on the `ProblemDetails`) that
  `AppException` has no equivalent for, and it is thrown by the pipeline
  behavior, not by a handler making a domain decision. Two different things
  that both happen to be 400.
- `DbUpdateConcurrencyException` and `TenantIsolationViolationException` also
  keep their cases: both come from outside the Application layer and carry no
  kind to read.
- Every response shape a client sees is unchanged by this refactor.
  `AuthenticationEndpointTests` and the existing handler unit tests passed
  untouched, which is the point — this is a generalization, not a new contract.
- Handlers must not throw bare `Exception`/`InvalidOperationException` for
  business rejections any more: those become 500s with `UnexpectedError`, which
  is the fallback for genuine bugs.

## Reason-code catalogue

`ReasonCodes` (`BookSpace.Application/Common/Errors/`) is the catalogue, with
the `ErrorKind` each code is thrown with recorded beside it. Adding a code
means adding it there **and** to CLAUDE.md §6's list; a literal at a throw
site is the thing this replaces.

| Code | Kind | Status | First thrown |
|---|---|---|---|
| `ResourceNotFound` | NotFound | 404 | WP-3 Phase 2 |
| `ResourceArchived` | RuleViolation | 422 | WP-3 Phase 2 |
| `InvalidTimeZone` | Validation | 400 | WP-3 Phase 2 |
| `CapacityBelowExistingBookings` | RuleViolation | 422 | WP-3 Phase 2 |
| `OverlappingAvailabilityWindow` | Conflict | 409 | WP-3 Phase 3 |
| `ApproversRequired` | RuleViolation | 422 | WP-3 Phase 3 |
| `ApproverNotEligible` | RuleViolation | 422 | WP-3 Phase 3 |
| `BlackoutPeriod` | RuleViolation | 422 | WP-3 Phase 4 |
| `SlotUnavailable` | Conflict | 409 | WP-4 |
| `CapacityExceeded` | Conflict | 409 | WP-4 |
| `OutsideAvailability` | RuleViolation | 422 | WP-4 |
| `ApprovalRequired` | RuleViolation | 422 | WP-4 |

Three notes on the choices:

- **The four booking codes are catalogued before they have throwers.** They
  are not speculative — CLAUDE.md §6 and FR-4.5 already committed to those
  exact strings — and listing them means WP-4 adds throwers rather than
  inventing spellings. `ReasonCodesTests` asserts all six §6 codes exist, so
  none can be quietly dropped in the meantime.
- **`ApproverNotEligible`, not `ApproverNotInTenant`** (which is what
  `docs/wp3-plan.md` called it). One code covers both ways an approver
  assignment fails — the user is in another tenant, or lacks the `Approver`
  role — and, unlike the plan's name, it does not tell the caller *which*.
  A code meaning "not in your tenant" would confirm the id exists somewhere,
  which is the cross-tenant disclosure AC-4 forbids.
- **`ApproverNotEligible` is RuleViolation, not NotFound.** Within the tenant
  the user genuinely does not exist, but the request is a well-formed
  assignment refused by a rule, and 404 on a PUT whose *own* path resource
  exists reads as "the resource is missing".

Authentication's five codes stay in `AuthenticationFailureReason` beside the
handlers that guarantee every credential failure looks identical (FR-2.1), so
that rationale sits with the codes it constrains. The cost is two files to
check when adding a code; `ReasonCodesTests` covers the risk that comes with
the split by asserting every code is unique across both, and that each code's
value matches its member name.

---

## Amendment (2026-09-01) — one named exception per failure

**Status:** Amended and implemented (2026-09-01). Everything above still holds
except the shape of the throw site; the parts that changed are marked below.
**Raised by:** the mentor, reviewing the WP-3 Phase 2 code.

### What was wrong with the original decision

`AppException(kind, reasonCode, message)` took the kind and the code as two
independent parameters. Nothing tied them together, so

- `ErrorKind.NotFound` alongside `ReasonCodes.ResourceArchived` compiled
  cleanly and would have shipped a 404 for something that must be a 422, and
- a throw site could pass a string literal instead of a catalogue constant.

The catalogue records the correct kind beside every code — **in a comment**.
The original record even acknowledged this, arguing that enforcing it would
mean consulting a code-to-kind map at every throw site to be worth anything.
That framing was the mistake: a named subclass fixes the pairing once, at
declaration, and costs nothing at the throw site.

### The amendment

**Each distinct failure is a `sealed` class deriving from `AppException`, fixing
its own kind and reason code in its constructor and composing its own log
message.** `AppException` is now **abstract with a protected constructor**, so a
bare one cannot be thrown at all — the rule is enforced by the compiler rather
than by remembering it, which is the same standard CLAUDE.md §4.2 sets for
tenant isolation.

Five classes exist, in `BookSpace.Application.Common.Errors` beside the
catalogue they draw from: `ResourceNotFoundException`,
`ResourceArchivedException`, `InvalidTimeZoneIdException`,
`ApproversRequiredException`, `CapacityBelowExistingBookingsException`.

**Deliberately unchanged:**

- `GlobalExceptionHandler` still has **one arm for the whole hierarchy** and
  still maps `ErrorKind` → status exactly once. It never learns a subclass
  name. This was the part worth protecting: a design where the handler grew a
  `case` per exception type would undo the reason the original decision existed,
  and WP-4's six booking rejections would each need API-layer plumbing.
- The **`ReasonCodes` catalogue stays**, and subclasses pass its constants. The
  string is the wire contract, one place to look, and `ReasonCodesTests` proves
  uniqueness across both catalogue files.
- The **message is still log-only**, and `ErrorKind` is unchanged.
- **`AuthenticationException` is unchanged** and is the one sanctioned exception
  to "one class per failure": it carries five different codes on purpose, so
  every credential failure looks identical to the client (FR-2.1). It was
  already a subclass, which is what made this amendment obvious in hindsight.

### Codes without a class yet

Seven catalogue codes have no thrower — `OverlappingAvailabilityWindow` and
`ApproverNotEligible` (WP-3 Phase 3), `BlackoutPeriod` (Phase 4), and WP-4's
`SlotUnavailable`, `CapacityExceeded`, `OutsideAvailability`,
`ApprovalRequired`. **No speculative classes were created for them.** The
abstract base makes that safe: a code with no class simply cannot be thrown, so
the phase that adds the thrower must add the class, and there is no way to
shortcut it with a bare `AppException`. Declaring the codes up front still does
its job — WP-4 adds throwers rather than inventing spellings.

### Naming

`<ReasonCode>Exception`, with one documented bend: the `InvalidTimeZone` code's
class is `InvalidTimeZoneIdException`, because **`System.InvalidTimeZoneException`
already exists** — `TimeZoneInfo` throws it for a corrupt timezone database,
which this codebase could plausibly encounter. Shadowing a BCL exception name is
how a `catch` ends up catching something nobody meant. The code itself stays
`InvalidTimeZone`: the wire contract should not be reshaped by what the BCL
happens to have named a type.

### What now enforces the pairing

`AppExceptionCatalogueTests` discovers every concrete `AppException` subclass by
reflection and asserts that each one appears in an expected `(code, kind)` table,
carries a code that exists in the catalogue, and has a non-empty message. It
also fails if the table lists a type that no longer exists. Adding a failure
therefore means writing the pairing down deliberately, and a mispaired kind
fails the build — verified by deliberately mispairing
`ResourceArchivedException` and watching it fail.

### Migration cost, for the record

Seven throw sites in `src`, five new classes, four unit-test files tightened
from "base type plus string comparison" to the specific type, and one test-local
`ProbeAppException` so the mapping tests can still exercise any kind. **The 142
integration tests did not change at all** — they assert `reasonCode` and status
over real HTTP, and the observable contract is identical. That is the clearest
evidence this was a refactor and not a redesign, and it was only that cheap
because the original decision funnelled everything through one base type and one
mapping point.
