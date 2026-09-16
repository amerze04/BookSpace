_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### Resource list filters extended for WP-7 — 2026-09-15

Not a work package, and not part of the hardening pass above — a small,
deliberate backend addition made *during* WP-7 planning
(`docs/wp7-plan.md`), after the owner reversed an earlier call. WP-7's
resource browse screen needed a search box and an "Approval" filter;
`GET /resources` had never supported either (only `type`, `includeArchived`,
and paging/sort), so the first answer was to filter client-side over one
fetched page rather than touch a backend endpoint from inside a frontend
package. The owner decided the opposite was worth doing properly instead.

`ListResourcesQueryRequest` gained two optional parameters: `Search`
(matches `Name` or `Description`, case sensitivity following the database's
own collation rather than anything decided in code — the same non-decision
this codebase makes everywhere else a string comparison isn't given its own
rule) and `RequiresApproval` (a nullable `bool`, not one defaulting to
`false` — unlike `IncludeArchived` there is no sensible default subset to
hide). `ListResourcesQueryRequestValidator` bounds `Search` to
`ResourceFieldRules.NameMaxLength` (200), the same ceiling `Name` itself
carries, so an oversized value is a 400 naming the field rather than
anything stranger. `ResourceRepository.ListAsync` applies both as ordinary
`Where` clauses, composing with the existing `Type`/`IncludeArchived`
filters rather than replacing them. `ResourcesController.ListResourcesRequest`
carries the two new query-string names through, per decision `0015`'s wire
shape convention.

No new reason code: an over-long search string is `ValidationFailed` like
any other oversized field, and there is no invalid state `RequiresApproval`
can be in beyond what model binding already rejects. 10 new integration
tests in `ResourceReadEndpointTests` cover both filters individually,
combined with `type`, case-insensitivity (against real SQL Server, not
assumed), the blank-search-means-no-filter case, and the length-ceiling
rejection. Test baseline: **1066 unit + 507 integration, 0 failed** (1066 +
497 immediately before this change).

The frontend side of this (WP-7 Phase 1 step 3) was already built against
the old, client-side answer earlier in the same session and needs redoing
against these real parameters — including a search-input debounce (~500ms)
now that every keystroke would otherwise cost an HTTP round-trip — tracked
in `docs/wp7-plan.md`'s Phase 1 section rather than here, since it isn't
shipped yet.
