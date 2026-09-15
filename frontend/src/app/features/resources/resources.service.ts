import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PagedResult } from '../../core/http/paged-result';
import { ListResourcesParams, ResourceDetail, ResourceSummary } from './resources.models';

// Thin wrapper over GET /resources and GET /resources/{id} — no filtering,
// ordering or caching here, same "bind and dispatch" thinness the backend's
// own ResourcesController keeps to. Every screen in features/resources/ goes
// through this rather than injecting HttpClient directly, so the request
// shape (query param names, base URL) lives in exactly one place.
@Injectable({ providedIn: 'root' })
export class ResourcesService {
  private readonly http = inject(HttpClient);

  list(params: ListResourcesParams = {}): Observable<PagedResult<ResourceSummary>> {
    return this.http.get<PagedResult<ResourceSummary>>(`${environment.apiBaseUrl}/resources`, {
      params: buildListParams(params),
    });
  }

  getById(id: string): Observable<ResourceDetail> {
    return this.http.get<ResourceDetail>(`${environment.apiBaseUrl}/resources/${id}`);
  }
}

// A field is only added when the caller actually set it — including
// `false` for includeArchived, which is why every check here is against
// `undefined` rather than truthiness. Omitting an unset field lets the
// backend's own documented default apply instead of this file guessing at
// a second copy of it (see ListResourcesParams).
function buildListParams(params: ListResourcesParams): HttpParams {
  let httpParams = new HttpParams();

  if (params.page !== undefined) {
    httpParams = httpParams.set('page', params.page);
  }
  if (params.pageSize !== undefined) {
    httpParams = httpParams.set('pageSize', params.pageSize);
  }
  if (params.sort !== undefined) {
    httpParams = httpParams.set('sort', params.sort);
  }
  if (params.includeArchived !== undefined) {
    httpParams = httpParams.set('includeArchived', params.includeArchived);
  }
  if (params.type !== undefined) {
    httpParams = httpParams.set('type', params.type);
  }

  return httpParams;
}
