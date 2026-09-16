_Extracted from CLAUDE.md §12 during the 2026-09-16 file-size reduction pass. This is the full delivery narrative; CLAUDE.md §12 keeps only the task/AC checklist and status for this WP._

---

### WP-6 — Angular Foundation & Auth — **Done** (2026-09-14)
Source doc: `docs/Work Packages - Week 5 and 6.pdf` (week 5, frontend track),
which carries WP-6 and WP-7 together.
**Plan: [`docs/wp6-plan.md`](docs/wp6-plan.md)**, approved 2026-09-11 before
any code was written — the same process every backend WP has gone through.
Two shape questions were settled with the owner first: the refresh token
stays body-based and client-held (no backend change — recorded as an amendment
to [`0011`](docs/decisions/0011-refresh-token-hashing-and-rotation.md) rather
than a new decision, since the token model itself didn't change), and state
management is Angular signals plus plain injectable services, no state
library. Built as a teaching exercise (the owner is new to Angular/frontend
generally) — each phase landed in small, explained steps rather than as one
commit; `docs/wp6-plan.md` records the real bugs found along the way, not just
the intended design.

- [x] Set up the Angular app with standalone components and sensible routing.
- [x] Build login; store and refresh tokens correctly on the client.
- [x] Add an HTTP interceptor that attaches auth and handles token refresh.
- [x] Add route guards so unauthenticated users can't reach protected pages.
- [x] Establish a state-management approach and stick to it.
- [x] Handle API errors gracefully in the UI.

Acceptance criteria — **all four met, verified against the real running
backend**:
- [x] A user logs in through the UI and reaches an authenticated area.
- [x] Protected routes are inaccessible without a valid session.
- [x] Token refresh happens transparently via the interceptor.
- [x] API errors surface as clear user feedback, not silent failures.

Phasing — full detail, every bug found, and the reasoning behind each design
choice in `docs/wp6-plan.md`:
1. **Foundations + auth core** — `AuthService` (signal-based session state,
   `login()`), `core/auth/jwt-decode.ts`, the Login screen
   (`features/auth/login/`) matching `design/login_page_design.png`. Found
   while building it: the JWT's role claim's real wire key is not `"role"` but
   the full `ClaimTypes.Role` URI — confirmed against a real token from the
   running backend, not assumed from decisions/0009's table.
2. **Interceptor & session lifecycle** — `core/auth/auth.interceptor.ts`
   (bearer attach, 401 → single-flight refresh → retry) and
   `AuthService.refreshAccessToken()`'s `shareReplay(1)`-backed single-flight
   guard. Verified live by corrupting the stored access token via the browser
   console and watching one refresh call fix a failing request transparently.
   Found and fixed: a zoneless-Angular bug where `LoginComponent`'s
   `submitting`/`errorMessage` were plain fields instead of signals, so the UI
   never updated after a failed login — converted to signals, matching the
   state-management decision above applied consistently.
3. **Route guards & authenticated shell** — `core/auth/auth.guard.ts`
   (`authGuard`, `guestOnlyGuard`, `approverGuard`), `layout/shell/`
   matching `design/auth_shell_design.png`, a shared `features/placeholder/`
   page, `shared/brand-mark/` (the login logo, extracted to a component on its
   third use). `AuthService.canApproveBookings` gates a role-aware "Approvals"
   nav item and its route, on the same `Approver`/`TenantAdmin` pair decision
   `0018` already treats as eligible. Two bugs found and fixed: the same
   field-initializer-ordering hazard as phase 1 recurred and was fixed for
   good by switching to `inject()` field initializers (which run in
   declaration order, constructor or not) instead of constructor parameters;
   and a crash reading route data at construction time, fixed by walking
   `router.routerState.snapshot` (the already-resolved tree) instead of the
   *live* `ActivatedRoute` tree, which isn't fully wired up yet at that exact
   moment. The owner corrected the breadcrumb design directly (flat tabs
   aren't children of Home) — flagged in the plan for WP-7, when real nesting
   arrives.
4. **Error handling, tests, AC sweep** — `core/notifications/`
   (`NotificationService` + `NotificationListComponent`, mounted once at the
   app root), `core/http/` (`problem-details.ts` — `errors`' keys confirmed
   **PascalCase**, matching FluentValidation's C# property names, not the
   wire's usual camelCase — `skip-error-toast.ts`, `error-toast.interceptor.ts`).
   Interceptor order is `[errorToastInterceptor, authInterceptor]` — the
   error-toast interceptor has to be outermost (same idea as ASP.NET Core
   middleware order) so it only ever sees what the auth interceptor couldn't
   already fix silently. `LoginComponent` gained real per-field validation,
   both client-side and mapped from a genuine backend `errors` response.
   **26 vitest tests, 0 failed**, across `jwt-decode`, `auth.service`,
   `auth.interceptor` (including the single-flight case), `error-toast.interceptor`,
   and `auth.guard` specs.

Notes:
- WP-6 builds no booking-facing screens (resource lists, availability, the
  booking form, the calendar, the approval queue) — those are WP-7. This
  package is only the shell WP-7's screens will sit inside.
- The decoded-JWT-claims rule is load-bearing from phase 1 onward: claims read
  client-side are for UI/nav convenience only, never an authorization
  boundary — the backend is the only place a permission is actually enforced.
  `approverGuard` and the role-aware nav item are both this: UI-only, backed
  by no server-side change.
- This app is **zoneless** (no `zone.js` in `package.json` — an Angular 22
  default for new projects). The practical consequence, found the hard way in
  phase 2: a template only reacts to a signal write or an Angular-recognized
  event, never a plain field mutated after an `await`. Worth remembering for
  every WP-7 component, not just the two this package already fixed.

