import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { skipErrorToast } from '../../../core/http/skip-error-toast';
import { AvailabilityParams, AvailabilityResponse } from '../models/availability.models';

// Thin wrapper over GET /resources/{id}/availability — no client-side
// filtering or caching here, the same "bind and dispatch" thinness
// ResourcesService keeps to. The availability screen (Phase 2, later steps)
// goes through this rather than injecting HttpClient directly.
//
// Skips the global error toast: the screen renders its own inline
// loading/error/empty states (including the archived-vs-closed distinction
// decision `0020` requires), so a toast on top would only be noise — the same
// reasoning ResourcesService and AuthService.login/logout already apply.
@Injectable({ providedIn: 'root' })
export class AvailabilityService {
  private readonly http = inject(HttpClient);

  get(resourceId: string, params: AvailabilityParams): Observable<AvailabilityResponse> {
    return this.http.get<AvailabilityResponse>(
      `${environment.apiBaseUrl}/resources/${resourceId}/availability`,
      {
        params: buildParams(params),
        context: skipErrorToast(),
      },
    );
  }
}

// quantity is only added when the caller actually set it, so an omitted
// value lets the backend's own documented default (1) apply instead of this
// file guessing at a second copy of it — same rule ListResourcesParams'
// includeArchived follows.
function buildParams(params: AvailabilityParams): HttpParams {
  let httpParams = new HttpParams().set('from', params.from).set('to', params.to);

  if (params.quantity !== undefined) {
    httpParams = httpParams.set('quantity', params.quantity);
  }

  return httpParams;
}
