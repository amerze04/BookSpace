import { Injectable, computed, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import {
  Observable,
  catchError,
  filter,
  finalize,
  firstValueFrom,
  from,
  fromEvent,
  map,
  of,
  race,
  shareReplay,
  switchMap,
  take,
  throwError,
  timer,
} from 'rxjs';
import { environment } from '../../../environments/environment';
import { skipErrorToast } from '../http/skip-error-toast';
import { DecodedAccessToken, decodeAccessToken, isAccessTokenExpired } from './jwt-decode';
import { isWebLocksSupported, releaseRefreshLock, runWithWebLock, tryAcquireRefreshLock } from './refresh-lock';

// Login and refresh both hand back this same shape — the backend's own name
// for it (TokenIssuer's output, decisions/0015) is IssuedTokens.
interface IssuedTokens {
  accessToken: string;
  expiresIn: number;
  refreshToken: string;
}

// decisions/0011's amendment: refresh token stays body-based and client-held,
// not an httpOnly cookie — settled again at WP-6, no backend change.
const ACCESS_TOKEN_KEY = 'bookspace.accessToken';
const REFRESH_TOKEN_KEY = 'bookspace.refreshToken';

// Every outcome in decisions/0011's refresh table that actually means "this
// token is no good" — expired, reused, or the account/org gone inactive — is
// an AuthenticationException, which GlobalExceptionHandler maps to 401
// (CLAUDE.md §6). Anything else (no response at all, a 5xx, a 429) is the
// transport or the server failing, not the backend saying no.
function isTerminalRefreshFailure(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 401;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly claimsSignal = signal<DecodedAccessToken | null>(this.readStoredClaims());

  readonly claims = this.claimsSignal.asReadonly();
  readonly isAuthenticated = computed(() => this.claimsSignal() !== null);

  // Same two roles decision 0018 admits as an eligible approver — kept here,
  // not duplicated at each call site, so "who can approve" has one answer.
  // This is a UI convenience only (which nav items/routes are reachable) —
  // the backend enforces the real rule independently on every request.
  readonly canApproveBookings = computed(() => {
    const roles = this.claimsSignal()?.roles ?? [];
    return roles.includes('Approver') || roles.includes('TenantAdmin');
  });

  // Admin console phase 2. Who the tenant administration screens are for.
  //
  // **`TenantAdmin` only — SysAdmin is deliberately excluded**, and it is worth
  // knowing why, because the backend's own `AuthorizationPolicies.TenantAdmin`
  // *does* admit SysAdmin by role and it would be easy to mirror that here.
  // Every admin endpoint also carries the `TenantMember` policy, which requires
  // the `orgId` claim — and a SysAdmin has no organization at all
  // (decisions/0009, PRD §2: the platform operator must never see tenant
  // content in routine operation). So a SysAdmin admitted here would be shown a
  // console on which every single request answers 403. Matching the *effective*
  // permission rather than the role name is what keeps the UI honest.
  //
  // Like canApproveBookings, this is a UI convenience only — which nav items
  // and routes are reachable. The backend enforces the real rule independently
  // on every request, so nothing here can grant anything.
  readonly isTenantAdmin = computed(() => (this.claimsSignal()?.roles ?? []).includes('TenantAdmin'));

  // Bumped by clearSession(). A refresh that was already in flight when the
  // session ended (logout, or a terminal refresh failure) captures the
  // generation at its own start and checks it again before ever writing a
  // result — so a stale success arriving afterward cannot resurrect a session
  // that has already been torn down.
  private sessionGeneration = 0;

  constructor(private readonly http: HttpClient) {
    // Cross-tab sync: tokens live in shared localStorage (decisions/0011's
    // amendment), but each tab has its own signals. The `storage` event only
    // ever fires in *other* tabs than the one that made the change, which is
    // exactly the notification this needs — a tab that logs out, logs in, or
    // rotates its own tokens already updates its own signals directly.
    window.addEventListener('storage', (event) => this.onStorageEvent(event));
  }

  private onStorageEvent(event: StorageEvent): void {
    if (event.key !== ACCESS_TOKEN_KEY && event.key !== REFRESH_TOKEN_KEY && event.key !== null) {
      return;
    }

    if (!this.accessToken || !this.refreshToken) {
      // Another tab logged out, or storage was cleared entirely — this tab's
      // session is over too, whether or not it had noticed yet.
      if (this.claimsSignal() !== null) {
        this.clearSession();
      }
      return;
    }

    // Another tab logged in, or rotated tokens via its own refresh.
    this.claimsSignal.set(this.readStoredClaims());
  }

  async login(email: string, password: string): Promise<void> {
    // Skips the global toast: LoginComponent renders its own inline
    // feedback (a top-of-form message, or per-field errors) for anything
    // this call can fail with — a second, generic toast on top would be
    // noise on top of the one failure this app already explains well.
    const response = await firstValueFrom(
      this.http.post<IssuedTokens>(
        `${environment.apiBaseUrl}/auth/login`,
        { email, password },
        { context: skipErrorToast() },
      ),
    );
    this.storeSession(response);
  }

  // decisions/0011: logout is 204 whether or not the token was recognized, so
  // a network failure here shouldn't stop the client from ending its own
  // session — the local state is cleared either way.
  async logout(): Promise<void> {
    const refreshToken = this.refreshToken;
    this.clearSession();

    if (refreshToken) {
      try {
        // Skips the global toast too: this failure is deliberately swallowed
        // below regardless, so showing one first would contradict that.
        await firstValueFrom(
          this.http.post(
            `${environment.apiBaseUrl}/auth/logout`,
            { refreshToken },
            { context: skipErrorToast() },
          ),
        );
      } catch {
        // Already logged out locally — nothing more to do.
      }
    }
  }

  clearSession(): void {
    this.sessionGeneration++;
    localStorage.removeItem(ACCESS_TOKEN_KEY);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    this.claimsSignal.set(null);
  }

  // Used by the route guards (see auth.guard.ts): true if there is a live
  // access token already, or refreshing gets one. False — never a thrown
  // error — is the only signal a guard needs to decide "send them to login".
  hasValidSession(): Observable<boolean> {
    const claims = this.claimsSignal();
    if (claims && !isAccessTokenExpired(claims)) {
      return of(true);
    }

    if (!this.refreshToken) {
      return of(false);
    }

    return this.refreshAccessToken().pipe(
      map(() => true),
      catchError(() => of(false)),
    );
  }

  // Single-flight within this tab: if a refresh is already in progress, every
  // caller gets that same in-flight Observable instead of starting a second
  // HTTP call. Cross-tab coordination (below) is the other half of the same
  // problem — both exist so several requests failing at once, in one tab or
  // several, cannot fire several concurrent POST /auth/refresh calls with the
  // same refresh token, which decisions/0011's reuse-detection would then see
  // as theft and kill the whole family over nothing.
  private refreshInFlight: Observable<string> | null = null;

  // A live lock is assumed abandoned after this long — long enough for a
  // real refresh round trip, short enough that a tab that died mid-refresh
  // doesn't wedge every other tab out of refreshing for the rest of the
  // session.
  private static readonly REFRESH_LOCK_TTL_MS = 8_000;

  // How long a tab that lost the lock waits for the leader's result before
  // assuming *it* died and taking a turn itself, rather than waiting forever.
  // Deliberately longer than REFRESH_LOCK_TTL_MS: a single wait is meant to
  // guarantee the lock has gone stale by the time this tab tries to take it
  // over, rather than looping through several waits first.
  private static readonly PEER_REFRESH_TIMEOUT_MS = 9_000;

  refreshAccessToken(): Observable<string> {
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }

    const refreshToken = this.refreshToken;
    if (!refreshToken) {
      this.clearSession();
      return throwError(() => new Error('No refresh token to refresh with.'));
    }

    this.refreshInFlight = this.startRefresh(refreshToken).pipe(
      finalize(() => {
        this.refreshInFlight = null;
      }),
      shareReplay(1),
    );

    return this.refreshInFlight;
  }

  // Web Locks (navigator.locks) is a true mutex — see refresh-lock.ts — so
  // every tab just runs the same coordinated function under it, rather than
  // this tab's own "am I the leader or a follower" branch. The localStorage
  // lock is the fallback for a browser without Web Locks, where that
  // leader/follower split (and its own timeout-based takeover) is still the
  // best available approximation.
  private startRefresh(refreshToken: string): Observable<string> {
    if (isWebLocksSupported()) {
      return from(runWithWebLock(() => firstValueFrom(this.performRefreshIfNeeded(refreshToken))));
    }

    const lockId = tryAcquireRefreshLock(AuthService.REFRESH_LOCK_TTL_MS);
    if (lockId) {
      return this.performRefreshIfNeeded(refreshToken).pipe(finalize(() => releaseRefreshLock(lockId)));
    }
    return this.waitForPeerRefresh(refreshToken);
  }

  // Re-checks storage before ever calling the network, against the refresh
  // token this call actually started with — not against local expiry, which
  // would wrongly no-op a caller that asked to refresh precisely because the
  // server just rejected an access token that still *looks* unexpired
  // locally. Only a refresh token that has changed since this call started
  // proves a peer tab already rotated it while this call waited for the
  // lock; handing decisions/0011's reuse-detection that same, now-stale
  // token again would look exactly like theft, so the token already in
  // storage is adopted instead of refreshing again.
  private performRefreshIfNeeded(refreshTokenAtStart: string): Observable<string> {
    const currentRefreshToken = this.refreshToken;
    if (!currentRefreshToken) {
      this.clearSession();
      return throwError(() => new Error('Session ended while waiting to refresh.'));
    }

    if (currentRefreshToken !== refreshTokenAtStart) {
      const currentAccessToken = this.accessToken;
      if (currentAccessToken) {
        this.claimsSignal.set(this.readStoredClaims());
        return of(currentAccessToken);
      }
      // Storage shows a rotation happened but no access token to show for
      // it (a rare interleaving) — fall through and refresh for real below,
      // with whatever refresh token is current now.
    }

    // Captured before the call goes out. logout() (or a terminal failure
    // elsewhere) bumps this — if it moves before the response comes back, the
    // session this response would restore no longer exists, and it must not
    // be resurrected (item 6: logout racing an in-flight refresh).
    const generationAtStart = this.sessionGeneration;

    return this.http.post<IssuedTokens>(`${environment.apiBaseUrl}/auth/refresh`, { refreshToken: currentRefreshToken }).pipe(
      map((response) => {
        if (this.sessionGeneration !== generationAtStart) {
          throw new Error('Session ended while a refresh was in flight; discarding its result.');
        }
        this.storeSession(response);
        return response.accessToken;
      }),
      catchError((error: unknown) => {
        // Only a terminal failure (the backend itself rejecting the refresh
        // token — expired, reused, or the account/org gone inactive; every
        // row in decisions/0011's table is a 401) means the session is over.
        // A network error, a 5xx, or a 429 says nothing about whether the
        // refresh token is still good, so the old tokens stay — the next
        // request gets to try again instead of being logged out by an outage.
        if (this.sessionGeneration === generationAtStart && isTerminalRefreshFailure(error)) {
          this.clearSession();
        }
        return throwError(() => error);
      }),
    );
  }

  // Lost the localStorage lock to another tab (Web Locks fallback path only).
  // Waits for that tab's result — signalled the only way a tab actually can
  // signal another one here, a `storage` event on the keys a refresh (or a
  // logout) touches — rather than making its own call. A timeout with no
  // event at all means the leader tab likely crashed or was closed
  // mid-refresh; this tab then takes its own turn.
  private waitForPeerRefresh(refreshToken: string): Observable<string> {
    return race(
      fromEvent<StorageEvent>(window, 'storage').pipe(
        filter((event) => event.key === ACCESS_TOKEN_KEY || event.key === REFRESH_TOKEN_KEY || event.key === null),
        take(1),
        switchMap(() => this.resolvePeerOutcome()),
      ),
      timer(AuthService.PEER_REFRESH_TIMEOUT_MS).pipe(
        switchMap(() => {
          // Re-read rather than reuse the argument: if the leader's refresh
          // actually succeeded but this tab somehow missed the storage event,
          // the token to retry with is whatever is current now, not the one
          // this wait started with.
          const currentRefreshToken = this.refreshToken;
          if (!currentRefreshToken) {
            this.clearSession();
            return throwError(() => new Error('Session ended while waiting for a peer tab to refresh.'));
          }
          return this.startRefresh(currentRefreshToken);
        }),
      ),
    );
  }

  // Re-reads localStorage rather than trusting the event's own newValue: the
  // event only says *something* changed, and what matters is the resulting
  // state, not which of possibly several writes produced it.
  private resolvePeerOutcome(): Observable<string> {
    const token = this.accessToken;
    if (token && this.refreshToken) {
      this.claimsSignal.set(this.readStoredClaims());
      return of(token);
    }

    this.clearSession();
    return throwError(() => new Error('Session ended in another tab while waiting for its refresh.'));
  }

  get accessToken(): string | null {
    return localStorage.getItem(ACCESS_TOKEN_KEY);
  }

  get refreshToken(): string | null {
    return localStorage.getItem(REFRESH_TOKEN_KEY);
  }

  private storeSession(response: IssuedTokens): void {
    localStorage.setItem(ACCESS_TOKEN_KEY, response.accessToken);
    localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
    this.claimsSignal.set(decodeAccessToken(response.accessToken));
  }

  private readStoredClaims(): DecodedAccessToken | null {
    const token = localStorage.getItem(ACCESS_TOKEN_KEY);
    if (!token) {
      return null;
    }

    try {
      return decodeAccessToken(token);
    } catch {
      return null;
    }
  }
}
