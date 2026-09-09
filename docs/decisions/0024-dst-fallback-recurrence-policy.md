# 0024 — DST fall-back policy for a recurring occurrence

## Status
Decided (2026-09-08), settled with the repo owner before any WP-5 code was
written. This is CLAUDE.md §9's last open item.

## Context
`0008` covers the spring-forward case for a recurring occurrence: a local time
that does not exist that day is skipped. It explicitly left the fall-back case
open — see its Notes section — because the two are not the same problem. A
missing local time has nothing to create a `Booking` from. An **ambiguous**
local time (clocks go back; 01:30 happens twice) names two real, valid UTC
instants, so "skip it" would throw away a slot that genuinely exists, and
"create it" needs a rule for *which* instant.

`0021` resolves the neighbouring question for an availability *window*: a range
absorbs the repeated hour by being 25 hours long that day (earlier offset for
the window's start, later for its end). That is a property of a *range*, and a
recurring occurrence is not one — `RecurrenceRule.LocalStartTime` and
`LocalEndTime` each name a single wall-clock instant on the occurrence's date,
not a boundary of something that is allowed to stretch. `0021`'s technique
does not transfer directly; what transfers is only the general policy of
"ambiguous → earlier."

`IResourceTimeZone` already has the mechanism this needs
(`ToUtcEarliest`/`ToUtcLatest`), from WP-3 Phase 5. What was missing was the
policy for which one a recurrence expansion should call, and this record is
that policy, not a schema or interface change.

## Decision
**A recurring occurrence's ambiguous local times resolve to the earlier UTC
instant — consistently, for both the occurrence's start and its end.**

This is deliberately **not** `0021`'s start-earlier/end-later split. `0021`
answers "how long did the window actually run" for a range that is allowed to
be 23 or 25 hours long; recurrence expansion is answering a different
question — "what instant is this occurrence's start, and what instant is its
end" — and each is looked up independently as a single wall-clock time via
`IResourceTimeZone.ToUtcEarliest`. Using earlier for both keeps the
occurrence's actual duration equal to its nominal one
(`LocalEndTime - LocalStartTime`) on the one day a year this local time is
ambiguous; mixing earlier-start with later-end would silently lengthen that
one occurrence by an hour, which is the same distortion `0021` chose to accept
for a *window* but has no reason to impose on a *booking*.

"Earlier" was chosen over "later" for the same reason `0021` picked it for a
window's start: it is the smaller of the two candidate answers, so applying it
uniformly can only ever make an occurrence's actual instant earlier than or
equal to what a caller who ignored the ambiguity would compute, never later —
consistent with the rest of this system's default of never silently keeping
someone out of a resource they are entitled to (the same asymmetry that makes
`CanBeCancelled` test `EndsAtUtc` rather than `StartsAtUtc`, and that makes an
availability window's start take the earlier offset). No per-org or per-rule
override exists; DST fall-back is a twice-a-year edge case on one occurrence
of a series, not something worth a configuration surface.

No occurrence is skipped in this case, unlike spring-forward — the whole point
is that both instants are real, so there is always something to create the
`Booking` from. `RecurrenceOccurrenceSkipped` and its notification therefore do
not apply here; nothing about the recurrence-materialization response changes
for a fall-back occurrence beyond it landing on the earlier of its two
possible instants, indistinguishably from any other occurrence.

## Consequences
- `RecurrenceExpansion` (WP-5, the application-layer expansion CLAUDE.md §4.3
  requires) calls `IResourceTimeZone.ToUtcEarliest` for **both** an
  occurrence's start and its end, never `ToUtcLatest`, and never the two-method
  split `AvailabilityWindowExpansion` uses for a window's boundaries. One
  method covers every occurrence uniformly; `ToUtcLatest` still exists for the
  window case `0021` already covers and is not otherwise called from
  recurrence code.
- **No new schema change.** Unlike `0008`, this case never produces a skipped
  occurrence, so it needs no new `Notifications` anchor and no new
  `NotificationKind`. `Notification.ForSkippedOccurrence` is not called for a
  fall-back occurrence.
- **No crash, no silent duplicate** — the acceptance criterion this record
  exists to satisfy. `IResourceTimeZone.ToUtcEarliest` already handles an
  ambiguous local time without throwing (`SystemResourceTimeZone.Resolve`), so
  nothing new has to be built in `Infrastructure`; WP-5 only has to call it
  correctly from the expansion loop instead of leaving the case for
  `TimeZoneInfo`'s own default (which assumes standard time — the *later*
  instant for a fall-back local time, the opposite of this decision).
- A regression test asserts the earlier of the two candidate instants is what
  gets stored on `Bookings.StartsAtUtc`/`EndsAtUtc` for an occurrence whose
  local start (or end) falls in a real ambiguous hour, verified against real
  tzdata the way `0021`'s tests are.
- CLAUDE.md §9's "Still open" list is now empty; this was its last item.
