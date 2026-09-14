# WP-6 — Angular Foundation & Auth

Source: `docs/Work Packages - Week 5 and 6.pdf` (week 5, frontend track), which
carries WP-6 and WP-7 together. **That PDF is the authority on scope** — the
task list in CLAUDE.md §12 is copied from it, and anything not in it is a gap
to flag rather than something to add on judgment (CLAUDE.md §11).

---

## Status

**Done — all four phases complete, 2026-09-14.** Two shape questions were put
to the repo owner before writing this plan, the same process WP-3 through
WP-5 each went through on the backend side; both are recorded in §3. All four
acceptance criteria are met and 26 vitest tests pass, 0 failed. Built as a
teaching exercise — the repo owner is new to Angular/frontend generally, so
each phase was delivered in small steps with an explanation of what changed,
how, and why, rather than landed as one commit. Real bugs found and fixed
along the way are recorded under each phase below, not smoothed over — they
were part of what made the exercise real.

---

## 1. What WP-6 owes

Copied from the source doc via CLAUDE.md §12.

- Set up the Angular app with standalone components and sensible routing.
- Build login; store and refresh tokens correctly on the client.
- Add an HTTP interceptor that attaches auth and handles token refresh.
- Add route guards so unauthenticated users can't reach protected pages.
- Establish a state-management approach and stick to it.
- Handle API errors gracefully in the UI.

Acceptance criteria:

- A user logs in through the UI and reaches an authenticated area.
- Protected routes are inaccessible without a valid session.
- Token refresh happens transparently via the interceptor.
- API errors surface as clear user feedback, not silent failures.

WP-6 builds none of the booking-facing screens — resource lists, availability,
the booking form, the calendar, the approval queue — those are WP-7. This
package's whole job is the shell WP-7's screens will live inside: a session
that survives a page reload, routes that refuse an unauthenticated visitor,
and a place to show an error when the API rejects something.

---

## 2. What already exists that WP-6 builds on

- **The Angular scaffold** — `frontend/`, Angular 22, standalone components by
  default, `vitest` already wired as the test runner (WP-0). `src/app/` is
  currently just the default shell (`app.ts`, `app.routes.ts` empty,
  `app.config.ts`) — nothing to unwind, WP-6 starts clean.
- **The auth contract** — `POST /auth/login`, `POST /auth/refresh`,
  `POST /auth/logout`, all `[AllowAnonymous]`, rate-limited. Login and refresh
  both return `{ accessToken, expiresIn, refreshToken }`
  (`LoginCommandResponse` / `RefreshTokenCommandResponse`); logout is 204
  regardless of whether the token was recognized. Decisions
  [`0009`](decisions/0009-jwt-claims-and-token-lifetimes.md) (claim shape,
  15-minute access / 14-day absolute refresh window) and
  [`0011`](decisions/0011-refresh-token-hashing-and-rotation.md) (rotation and
  reuse-detection table below) are the contract this phase's client code has
  to match, not something WP-6 gets to redesign.
- **No `/me` endpoint exists.** Nothing hands the client its own user record.
  The only source for `orgId` / `role` / `email` is the JWT itself, so the
  client decodes it — for UI/nav convenience only, never as an authorization
  boundary. The backend enforces every real permission; a decoded claim just
  tells the UI what to show.
- **CORS is already configured for local dev** —
  `appsettings.Development.json` allows `http://localhost:4200`, Angular's
  default dev-server port. Nothing to change there.
- **The reason-code / `ProblemDetails` contract** (CLAUDE.md §6,
  [`0016`](decisions/0016-error-contract-and-reason-codes.md)) is what the
  error-handling phase maps into UI feedback: every error is a `ProblemDetails`
  body carrying a `reasonCode`, a `correlationId`, and — for validation
  failures — per-field `errors`. The client never sees a raw exception
  message.

## 3. Settled before planning

Two questions were put to the owner on 2026-09-11, because both have a real
trade-off and both shape the rest of this plan.

**Refresh token stays body-based, held by the client — no backend change.**
Decision `0011` flagged this as "worth revisiting once the Angular app lands."
It has now landed, and the answer is: no change. Switching to an httpOnly
cookie would remove XSS exposure on the refresh token, but it also requires
backend work this week (`AllowCredentials`, `Set-Cookie` on three endpoints, a
CSRF story) that isn't in WP-6's frontend-only task list. Recorded as an
amendment to `0011` rather than a new decision, since it doesn't change the
token model, only re-confirms the existing choice.

**State management is Angular signals + plain injectable services — no
state-management library.** Angular 22 has first-class signal support already
in `package.json`; a `BehaviorSubject`-per-service or NgRx store would both
work, but signals keep state as ordinary TypeScript with no new dependency,
and it's the closest analogue to the backend's plain-class style (relevant
since the owner is new to Angular and will have to defend this choice the same
way as everything on the backend). Revisit only if WP-7's calendar state turns
out to need more than services expose — flagged again in that package's own
plan, not assumed here.

---

## 4. Screens to design — one place, all four phases

You said you'd prepare designs as we reach them, not up front. Here's the full
list, so nothing lands mid-phase with no design to build against; each also
appears under its phase below.

| Phase | Screen / component | Notes |
|---|---|---|
| 1 | **Login** | Email + password fields, submit button, and — even though phase 4 is what wires up styling for it — leave room in the design for a top-of-form error message and per-field validation text now, so phase 4 doesn't need a second pass at this screen. |
| 1 | **Post-login placeholder** | Not a real design — a bare "you're in" page proving the session works, thrown away when phase 3's shell replaces it. Explicitly *not* worth your time to design. |
| 2 | *(none)* | Interceptor and refresh logic are invisible to the UI. |
| 3 | **Authenticated shell / app frame** | The nav chrome every WP-7 screen will sit inside: app name/logo area, a signed-in indicator (email is enough — no `/me` endpoint, so nothing richer is available), a logout button, and at least one nav item whose visibility depends on role (e.g. an "Approvals" link shown only to an `Approver`/`TenantAdmin`, per §3's decoded-claim rule) so the design shows both the member and the approver state of the same shell. |
| 4 | **Error feedback (toast/banner)** | One reusable component for a request-level failure (network error, 500, a 409 conflict) — where it appears on screen and how it dismisses. |
| 4 | **Login form validation states** | Addendum to phase 1's Login design, not a new screen: what a 400 with per-field `errors` looks like under each field, and what a generic `InvalidCredentials` 401 looks like as a top-of-form message. |

---

## 5. Phasing

Four phases, each a working increment against the real backend — no mocks,
same delivery style as the backend work packages. Control returns to the owner
between phases.

### Phase 1 — Foundations + auth core — **Done 2026-09-14**

- Project conventions: standalone components, a feature-folder layout
  (`core/`, `features/auth/`, `shared/` — `features/` grows in WP-7),
  `environment.ts` / `environment.development.ts` holding the API base URL.
- `AuthService`: `login()`, `logout()`, and session state as signals —
  `accessToken`, decoded claims (`sub`/`email`/`orgId`/`role`), and an
  `isAuthenticated` computed signal. A small JWT-decode utility (decode only,
  no verification — the backend already verified it; the client is just
  reading what it was handed).
- Login page wired to the real `POST /auth/login`.
- Routing skeleton: a public `login` route and one placeholder authenticated
  route (see §4) — just enough to prove a session survives.

**Screens needed:** Login, and the throwaway post-login placeholder (§4).

**Demo:** log in through the UI with a seeded account, land on the
placeholder, confirm the decoded claims (email, role, orgId) are correct for
that account.

Delivered as planned: `environment.ts` (dev-only, no production split — no
deployed frontend target exists yet to justify one), `core/auth/jwt-decode.ts`,
`core/auth/auth.service.ts`, `features/auth/login/`. One real finding: **the
`role` claim's actual JSON key is not `"role"`**. `JwtAccessTokenService`
builds the token directly from a `Claim` list rather than through
`JwtSecurityTokenHandler`'s short-name mapping, so the wire key is the full
`ClaimTypes.Role` URI
(`http://schemas.microsoft.com/ws/2008/06/identity/claims/role`) — confirmed
by logging in against the real running backend and decoding a genuine token,
not by reading decisions/0009's table (which calls it `role` for readability).
`jwt-decode.ts` documents this and `jwt-decode.spec.ts` pins it down so it
can't silently regress.

### Phase 2 — Interceptor & session lifecycle — **Done 2026-09-14**

- HTTP interceptor: attaches `Authorization: Bearer` to outgoing requests.
- On a 401, attempts exactly one silent refresh — **single-flight**, so several
  requests failing at once trigger one `POST /auth/refresh`, not one each,
  and every queued request retries against the new token once it lands.
- A refresh failure (`RefreshTokenExpired`, `RefreshTokenReuseDetected`,
  `AccountInactive` — decision `0011`'s table) clears session state and sends
  the user to login, rather than looping or surfacing a raw 401.
- `POST /auth/logout` wired to the logout button (reusing phase 1's
  placeholder page for now — phase 3 gives it a real home).

**Screens needed:** none — this phase is invisible to the UI.

**This is WP-6's own hard problem**, the frontend analogue of the backend's
concurrency work: get the single-flight queueing wrong and either several
refresh calls race each other (burning through rotation, decision `0011`'s
reuse-detection would then kill the whole family on the client's own
concurrent requests) or a request left waiting never gets retried.

**Demo:** let an access token expire (or force it), fire a couple of API
calls, confirm exactly one refresh call happens and both requests complete
successfully; then exhaust the refresh token and confirm a clean redirect to
login.

Delivered as planned: `core/auth/auth.interceptor.ts` (bearer attach + 401
catch/refresh/retry) and `AuthService.refreshAccessToken()`'s single-flight
guard (`shareReplay(1)` over a cached in-flight `Observable`, cleared via
`finalize`). Verified live by corrupting the stored access token in
`localStorage` via the browser console and watching the Network tab: one
failed request, one `POST /auth/refresh`, one successful retry — with the
corrupted-refresh-token variant confirming a clean redirect to `/login`
instead.

**A real bug found here, not just an anticipated one:** the login page's
"Signing in…" state never reverted after a failed login. Root cause — this
project is **zoneless** (no `zone.js` in `package.json`, an Angular 22
default), and `submitting`/`errorMessage` were plain class fields, not
signals. A write to a plain field inside a `catch`/`finally` block after an
`await` has nothing telling Angular to re-render, since there's no zone
patching the `Promise` continuation and no signal write to notice either.
Fixed by converting both to signals — the same rule §3's state-management
answer already implied, just not yet applied to a component's own local UI
state, only to `AuthService`'s shared state. Written up as a `LoginComponent`
comment so the reasoning survives the fix.

### Phase 3 — Route guards & authenticated shell — **Done 2026-09-14**

- Functional `CanActivateFn` guards: unauthenticated → redirect to `login`
  with a `returnUrl`; authenticated → kept off `login` itself.
- The real authenticated shell replaces phase 1's placeholder: nav chrome,
  signed-in indicator, logout button, and the first role-aware nav item
  (reading the decoded `role` claim from `AuthService`).

**Screens needed:** the authenticated shell / app frame (§4), in both its
member and approver-visible states.

**Demo:** an unauthenticated visit to the protected route bounces to login and
returns to the originally-requested route after a successful login; a
member's shell and an approver's shell visibly differ by one nav item.

Delivered as planned, plus a fourth nav route beyond the original screen list:
`core/auth/auth.guard.ts` (`authGuard`, `guestOnlyGuard`, and `approverGuard` —
the last one added in this phase, gating a new `/approvals` route the shell
design's approver-only nav item needed somewhere to point), the real
`layout/shell/shell.component.*`, a shared `features/placeholder/` page every
nav item routes to for now, and `shared/brand-mark/` — the login page's logo
mark, extracted into a component on its third use (the sidebar), per the "extract
on the third occurrence" rule rather than up front. `primaryNavItems` is a
`computed()` signal, not a plain array, gated on a new
`AuthService.canApproveBookings` (the same `Approver`/`TenantAdmin` pair
decision `0018` already treats as eligible) — UI convenience only, since the
real enforcement is `approverGuard` on the route and the backend's own RBAC
underneath that regardless.

**One breadcrumb design correction, made by the owner directly in the code:**
the first pass rendered every page's breadcrumb as `Home > <page>`, implying
Resources/My Bookings/etc. are children of Home. They aren't — every current
route is a flat, top-level sibling — so the owner simplified `breadcrumb` to
just `[title]` and flagged the real design question for later: once WP-7 adds
genuine nesting (a Rooms page under Resources), the breadcrumb needs to walk
every matched level of the *actual* route tree and collect each one's own
title, not synthesize an ancestor no route has.

**Two real bugs found and fixed, both instructive:**
- The same field-initializer-ordering bug as Phase 1's `LoginComponent`
  (`formBuilder` used before the constructor assigned it) recurred in
  `ShellComponent`, this time with `router`. Rather than move the affected
  code into the constructor again, the fix this time was to adopt `inject()`
  field initializers everywhere in this component instead of constructor
  parameters — field initializers run top-to-bottom in declaration order
  regardless of the constructor, which removes the whole ordering hazard
  rather than working around one instance of it.
- The shell crashed on load with `Cannot read properties of undefined
  (reading 'data')`. Cause: `currentRouteTitle()` walked
  `this.activatedRoute.firstChild` — the *live* `ActivatedRoute` tree — which
  isn't fully wired together yet at the exact moment `ShellComponent`'s own
  constructor runs. Fixed by walking `router.routerState.snapshot` instead —
  the router's already-fully-resolved snapshot tree for the current
  navigation, safe to read at construction time because nothing about it is
  still being assembled.

### Phase 4 — Error handling, tests, AC sweep — **Done 2026-09-14**

- A global mapping from the backend's `ProblemDetails` shape to user-facing
  feedback: validation failures surface per-field (login form first, WP-7's
  forms reuse the same mechanism), everything else surfaces through the
  toast/banner component — never a raw stack trace, never a silently dropped
  request.
- `vitest` coverage: `AuthService` (login/logout/claim decoding), the
  interceptor's refresh-and-queue logic (including the single-flight case),
  and the guards.
- Manual walkthrough of all four WP-6 acceptance criteria against the real
  running backend.
- Write-up: this plan's outcomes recorded back into CLAUDE.md §12, same as
  every backend WP.

**Screens needed:** the error toast/banner component, and the validation-state
addendum to the Login screen (§4) — both should exist before this phase
starts so the phase is wiring, not design-blocked.

**Demo:** the four ACs, shown live: login → authenticated area; a direct visit
to a protected route while logged out; a token refresh happening silently
mid-session; and a deliberately-triggered API error (e.g. wrong password, or
the backend stopped) rendering as a clear message rather than a blank screen
or a console error.

No design existed for the toast/banner (unlike Login and the shell), so it
was built directly from the brand doc's own rules — white surface, the
existing radius token, a colored left border in the semantic error/primary
color, a soft shadow rather than heavy elevation — isolated enough
(`core/notifications/`) to restyle later without touching the logic.

Delivered: `core/notifications/` (`NotificationService` — a signal-backed
list, `show()`/`dismiss()` — and `NotificationListComponent`, mounted once at
the app root so it's visible over both the login page and the shell
regardless of route); `core/http/problem-details.ts` (the `ProblemDetails`
shape, confirmed against a **real** response rather than assumed — notably,
`errors`' keys are **PascalCase** — `"Email"`, `"Password"` — matching the C#
request property names FluentValidation reports against, not the wire's usual
camelCase); `core/http/skip-error-toast.ts` (an `HttpContextToken` a request
can opt out with, for a caller that already has its own inline feedback); and
`core/http/error-toast.interceptor.ts`, which shows a toast for anything that
reaches it **except** a `401` (always `auth.interceptor`'s domain — it either
fixes it silently or is already redirecting with its own message) or a
request marked `skipErrorToast()`.

**Interceptor ordering was the one place a subtle mistake was cheap to make.**
`provideHttpClient(withInterceptors([errorToastInterceptor, authInterceptor]))`
— `errorToastInterceptor` has to be *first* (outermost, same idea as ASP.NET
Core middleware order), so it only ever sees whatever `authInterceptor`
couldn't already resolve. Reversed, the toast would fire on every
expired-token `401` before the silent refresh got a chance to run at all,
turning Phase 2's whole point into a toast flashing on screen every 15
minutes.

`AuthService.login()` and `.logout()` both now pass `context: skipErrorToast()`
on their own calls — `login()` because `LoginComponent` renders its own
feedback for anything it can fail with, `logout()` because decision `0011`
already means that failure is deliberately swallowed, and a toast appearing
during a silent logout would contradict that. The interceptor's own
refresh-failure branch now also calls `notifications.show(...)` directly
("Your session has expired. Please sign in again.") before redirecting,
closing the "why did I just get logged out" gap Phase 2 left open.

`LoginComponent` gained real field-level validation: `touchedFields` and
`fieldErrors` signals (touched-state tracked explicitly via `(blur)` handlers,
not read off `FormControl.touched` directly in the template — the same
"template reads a signal, always" rule Phase 1's bug established), client-side
messages for `required`/`email` once a field has been touched or submit was
attempted, and a mapping from a real backend `errors` dictionary onto the same
per-field slots (translating `Email`/`Password` to the form's `email`/
`password` control names — the PascalCase finding above, made concrete).

**Test coverage** (26 vitest tests, 0 failed, across 6 files): `jwt-decode.spec.ts`
(the role-claim URI, single vs. array roles, a missing `orgId`, a malformed
token); `auth.service.spec.ts` (login stores + decodes, logout clears locally
even when the server call fails, the single-flight refresh case via
`HttpTestingController.expectOne` — which throws if a second concurrent
request also went out — and a failed refresh clearing the session);
`auth.interceptor.spec.ts` (bearer attachment, 401 → refresh → retry,
single-flight across two simultaneously-failing requests, auth endpoints
never being retried, and the session-expired notification + redirect);
`error-toast.interceptor.spec.ts` (a mapped `ProblemDetails` title, the
network-failure fallback, silence on a `401`, silence on an opted-out
request); `auth.guard.spec.ts` (all three guards). One minor test-authoring
finding: three `approverGuard` assertions originally lived in one `it()`
block and the third's `router.serializeUrl(...)` call threw
(`Cannot read properties of undefined (reading 'hasChildren')`) — splitting
into three separate `it()` blocks, each getting its own fresh `Router` from
`beforeEach`, fixed it and left better-isolated tests regardless of the exact
cause.

**AC sweep**, against the real running backend:
- *A user logs in through the UI and reaches an authenticated area.* Met —
  `member1@acme.test` / `Passw0rd!` through the real login form lands on the
  shell's Home tab with decoded claims correctly reflected in the nav.
- *Protected routes are inaccessible without a valid session.* Met — a direct
  visit to any shell route while logged out redirects to `/login` with a
  `returnUrl`; `/approvals` additionally redirects a non-approver to `/home`
  even when authenticated.
- *Token refresh happens transparently via the interceptor.* Met — proved
  live in Phase 2 (corrupted access token → silent refresh → retry succeeds)
  and now also by `auth.interceptor.spec.ts`.
- *API errors surface as clear user feedback, not silent failures.* Met — a
  wrong password shows inline on the login form; anything else reaching the
  interceptor chain surfaces as a toast; nothing is dropped silently.

---

## 6. Notes

- No styling investment beyond what each phase's design calls for — WP-7 is
  where the real screens (and their volume of components) arrive, and
  re-styling the shell twice would be wasted work.
- The decoded-JWT-claims rule (§2) is the one thing worth being strict about
  from phase 1 onward: it is tempting to let a nav-visibility check quietly
  become a security check later, and it must not, since the backend is the
  only place that enforcement is real.
- Nothing here touches the backend. If phase 2 or 4 turns up a genuine
  contract gap (an error shape that doesn't fit `ProblemDetails`, a race the
  interceptor can't resolve client-side alone), that's a stop-and-ask per
  CLAUDE.md §11, not a silent backend patch mid-frontend-WP.
