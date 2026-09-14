import { Injectable, computed, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { DecodedAccessToken, decodeAccessToken } from './jwt-decode';

interface LoginResponse {
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

  constructor(private readonly http: HttpClient) {}

  async login(email: string, password: string): Promise<void> {
    const response = await firstValueFrom(
      this.http.post<LoginResponse>(`${environment.apiBaseUrl}/auth/login`, { email, password }),
    );
    this.storeSession(response);
  }

  get accessToken(): string | null {
    return localStorage.getItem(ACCESS_TOKEN_KEY);
  }

  get refreshToken(): string | null {
    return localStorage.getItem(REFRESH_TOKEN_KEY);
  }

  private storeSession(response: LoginResponse): void {
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
