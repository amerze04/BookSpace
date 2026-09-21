import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { PagedResult } from '../../../core/http/paged-result';
import { skipErrorToast } from '../../../core/http/skip-error-toast';
import { BlackoutPeriodSummary, ListBlackoutPeriodsParams } from '../models/blackout-periods.models';

// Thin wrapper over GET /resources/{id}/blackout-periods — same "bind and
// dispatch" thinness as ResourcesService and AvailabilityService.
//
// Skips the global error toast for a reason specific to this call: the
// availability grid works perfectly well without it. Blackouts only ever
// *label* a gap the availability response already excluded, so a failure here
// costs a reason, never correctness — the screen falls back to showing those
// gaps unlabelled rather than raising an error over them.
@Injectable({ providedIn: 'root' })
export class BlackoutPeriodsService {
  private readonly http = inject(HttpClient);

  list(
    resourceId: string,
    params: ListBlackoutPeriodsParams = {},
  ): Observable<PagedResult<BlackoutPeriodSummary>> {
    return this.http.get<PagedResult<BlackoutPeriodSummary>>(
      `${environment.apiBaseUrl}/resources/${resourceId}/blackout-periods`,
      { params: buildParams(params), context: skipErrorToast() },
    );
  }
}

function buildParams(params: ListBlackoutPeriodsParams): HttpParams {
  let httpParams = new HttpParams();

  if (params.from !== undefined) {
    httpParams = httpParams.set('from', params.from);
  }
  if (params.to !== undefined) {
    httpParams = httpParams.set('to', params.to);
  }
  if (params.page !== undefined) {
    httpParams = httpParams.set('page', params.page);
  }
  if (params.pageSize !== undefined) {
    httpParams = httpParams.set('pageSize', params.pageSize);
  }
  if (params.sort !== undefined) {
    httpParams = httpParams.set('sort', params.sort);
  }

  return httpParams;
}
