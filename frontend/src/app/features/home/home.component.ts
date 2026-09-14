import { Component, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';
import { AuthService } from '../../core/auth/auth.service';

// Throwaway — proves phase 1's session (and now phase 2's interceptor) work.
// Phase 3 replaces this with the real authenticated shell (wp6-plan.md §4/§5).
@Component({
  selector: 'app-home',
  imports: [],
  templateUrl: './home.component.html',
})
export class HomeComponent {
  protected readonly resourceCount = signal<number | null>(null);
  protected readonly callError = signal<string | null>(null);

  constructor(
    protected readonly auth: AuthService,
    private readonly http: HttpClient,
    private readonly router: Router,
  ) {}

  // Exercises the interceptor against a real protected endpoint — there's
  // nothing else in the app yet that calls the API after login.
  protected async callProtectedEndpoint(): Promise<void> {
    this.callError.set(null);
    try {
      const result = await firstValueFrom(
        this.http.get<{ totalCount: number }>(`${environment.apiBaseUrl}/resources`),
      );
      this.resourceCount.set(result.totalCount);
    } catch {
      this.callError.set('Call failed — see the network tab.');
    }
  }

  protected async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }
}
