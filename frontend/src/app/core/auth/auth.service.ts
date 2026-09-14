import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, finalize, firstValueFrom, map, shareReplay, tap, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';
import { DecodedAccessToken, decodeAccessToken } from './jwt-decode';

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

  constructor(private readonly http: HttpClient) {}

  async login(email: string, password: string): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<IssuedTokens>(`${environment.apiBaseUrl}/auth/login`, { email, password }),
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
        await firstValueFrom(this.http.post(`${environment.apiBaseUrl}/auth/logout`, { refreshToken }));
      } catch {
        // Already logged out locally — nothing more to do.
      }
    }
  }

  clearSession(): void {
    localStorage.removeItem(ACCESS_TOKEN_KEY);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    this.claimsSignal.set(null);
  }

  // Single-flight: if a refresh is already in progress, every caller gets
  // that same in-flight Observable instead of starting a second HTTP call.
  // This is what stops several requests failing at once (a whole page's worth
  // of calls after the access token expires) from firing several concurrent
  // POST /auth/refresh calls — which decisions/0011's reuse-detection would
  // then see as a stolen token and kill the whole family over nothing.
  private refreshInFlight: Observable<string> | null = null;

  refreshAccessToken(): Observable<string> {
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }

    const refreshToken = this.refreshToken;
    if (!refreshToken) {
      this.clearSession();
      return throwError(() => new Error('No refresh token to refresh with.'));
    }

    this.refreshInFlight = this.http
      .post<IssuedTokens>(`${environment.apiBaseUrl}/auth/refresh`, { refreshToken })
      .pipe(
        tap((response) => this.storeSession(response)),
        map((response) => response.accessToken),
        catchError((error) => {
          // Every failure row in 0011's table (expired / reused / account
          // inactive) means the session is over — there's no partial-failure
          // case worth keeping the old tokens around for.
          this.clearSession();
          return throwError(() => error);
        }),
        finalize(() => {
          this.refreshInFlight = null;
        }),
        shareReplay(1),
      );

    return this.refreshInFlight;
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
