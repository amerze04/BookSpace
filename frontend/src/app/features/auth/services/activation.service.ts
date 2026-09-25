import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { skipErrorToast } from '../../../core/http/skip-error-toast';

// `POST /auth/activate` — redeeming an invitation.
//
// **Deliberately not on `AuthService`**, which owns the session: tokens,
// refresh, the cross-tab lock. This endpoint mints nothing. It returns 204 and
// the caller then goes to the login screen like anyone else, which is the
// server's own design (user management phase 2) — handing back a session here
// would have meant re-implementing login's FR-2.4 account-state gate in a
// second place. Putting it on AuthService would suggest it participates in the
// session, and the next reader would look for a token it never returns.
//
// Skips the global error toast: the activation screen renders its own inline
// refusal, and the one thing that refusal must not do is say more than the
// server did.
@Injectable({ providedIn: 'root' })
export class ActivationService {
  private readonly http = inject(HttpClient);

  // 204 on success, so nothing comes back and nothing is returned.
  //
  // The token is the one from the emailed link. It is a credential, so it is
  // passed straight through and never logged, stored, or put anywhere but this
  // request body.
  activate(token: string, password: string): Observable<void> {
    return this.http.post<void>(
      `${environment.apiBaseUrl}/auth/activate`,
      { token, password },
      { context: skipErrorToast() },
    );
  }
}
