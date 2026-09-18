import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { skipErrorToast } from '../../../core/http/skip-error-toast';
import {
  CancelRecurrenceSeriesRequest,
  CancelRecurrenceSeriesResponse,
  CreateRecurrenceSeriesRequest,
  CreateRecurrenceSeriesResponse,
} from '../models/recurrence.models';

// Thin wrapper over POST /recurrence-rules, same thinness as
// BookingsService next door and the same skipErrorToast reasoning: an
// all-refused series is a 422 whose per-occurrence breakdown this screen
// renders in full (FR-5.4), not something a toast could usefully summarise.
@Injectable({ providedIn: 'root' })
export class RecurrenceRulesService {
  private readonly http = inject(HttpClient);

  // **The idempotency key is a required parameter even though the header is
  // optional server-side.** A caller that forgets it gets today's behaviour —
  // a fresh RecurrenceRule and a fresh attempt at every occurrence on any
  // retry — which is precisely what `RecurrenceCreationOperation` (the
  // 2026-09-15 hardening pass, item 11) exists to prevent. Making it a
  // parameter the compiler insists on means the decision is taken at the call
  // site, where the attempt's own lifecycle is known, rather than defaulted
  // away here.
  //
  // The lifecycle itself belongs to the caller (step 7) because getting it
  // backwards fails in both directions: one key per submission *attempt*,
  // reused only when retrying that same attempt after a transport failure,
  // regenerated whenever the form changes and is submitted again. Reuse it too
  // eagerly and a deliberate second series silently resolves to the first;
  // regenerate it on a retry and a crash-resumed request creates a duplicate
  // series.
  //
  // A header rather than a body field, matching
  // RecurrenceRulesController.Create's own [FromHeader(Name = "Idempotency-Key")]:
  // it describes the HTTP attempt, not the series being created — the same
  // distinction the correlation id already makes.
  create(
    request: CreateRecurrenceSeriesRequest,
    idempotencyKey: string,
  ): Observable<CreateRecurrenceSeriesResponse> {
    return this.http.post<CreateRecurrenceSeriesResponse>(
      `${environment.apiBaseUrl}/recurrence-rules`,
      request,
      {
        headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }),
        context: skipErrorToast(),
      },
    );
  }

  // FR-5.3. Cancels the series **and every occurrence still worth cancelling**
  // — not just the rule, since a "cancelled" series whose future occurrences
  // kept running would not be cancelled in any sense a member cares about.
  //
  // **No idempotency key here, unlike create, and that is not an oversight.**
  // The endpoint takes none: it inherits the single cancel's non-idempotency
  // one level up (a repeat rewrites the actor and time on every occurrence it
  // touches), so there is nothing for a key to resolve to and nothing above
  // this method retries it.
  cancel(
    recurrenceRuleId: string,
    request: CancelRecurrenceSeriesRequest,
  ): Observable<CancelRecurrenceSeriesResponse> {
    return this.http.post<CancelRecurrenceSeriesResponse>(
      `${environment.apiBaseUrl}/recurrence-rules/${recurrenceRuleId}/cancel`,
      request,
      { context: skipErrorToast() },
    );
  }
}
