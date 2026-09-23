import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { PagedResult } from '../../../core/http/paged-result';
import { skipErrorToast } from '../../../core/http/skip-error-toast';
import {
  ArchiveResourceResponse,
  AvailabilityWindowInput,
  CreateResourceRequest,
  CreateResourceResponse,
  ListResourcesParams,
  ReplaceApproversResponse,
  ReplaceAvailabilityWindowsResponse,
  ResourceDetail,
  ResourceSummary,
  UpdateResourceRequest,
  UpdateResourceResponse,
} from '../models/resources.models';

// Thin wrapper over GET /resources and GET /resources/{id} — no filtering,
// ordering or caching here, same "bind and dispatch" thinness the backend's
// own ResourcesController keeps to. Every screen in features/resources/ goes
// through this rather than injecting HttpClient directly, so the request
// shape (query param names, base URL) lives in exactly one place.
//
// Both calls skip the global error toast: every current caller
// (ResourceListComponent, ResourceDetailComponent) renders its own inline
// error/retry state for exactly this failure, so the toast would only be
// noise on top of it — the same reasoning AuthService.login/logout already
// apply to their own calls.
@Injectable({ providedIn: 'root' })
export class ResourcesService {
  private readonly http = inject(HttpClient);

  list(params: ListResourcesParams = {}): Observable<PagedResult<ResourceSummary>> {
    return this.http.get<PagedResult<ResourceSummary>>(`${environment.apiBaseUrl}/resources`, {
      params: buildListParams(params),
      context: skipErrorToast(),
    });
  }

  getById(id: string): Observable<ResourceDetail> {
    return this.http.get<ResourceDetail>(`${environment.apiBaseUrl}/resources/${id}`, {
      context: skipErrorToast(),
    });
  }

  // ---- Writes (admin console phase 3) ----
  //
  // TenantAdmin-only server-side; nothing here re-checks that, the same way no
  // read here re-checks tenancy. The admin screens live under `features/admin/`
  // and call through this service rather than injecting HttpClient, matching
  // how the decision panel lives under `approvals/` and reads through
  // `booking/`'s service: the *contract* belongs with the aggregate, the
  // *screen* belongs with the job it does. It also keeps every `/resources`
  // request shape in one file, which was this service's reason for existing.
  //
  // All three skip the global toast for the same reason the reads do: the admin
  // form renders every one of these failures inline, worded by
  // `resource-rejection.ts`, and a generic toast on top would be noise over the
  // one explanation that actually helps.

  create(request: CreateResourceRequest): Observable<CreateResourceResponse> {
    return this.http.post<CreateResourceResponse>(`${environment.apiBaseUrl}/resources`, request, {
      context: skipErrorToast(),
    });
  }

  // PUT, not PATCH: the body is a full representation and an omitted nullable
  // field means cleared (see UpdateResourceRequest).
  update(id: string, request: UpdateResourceRequest): Observable<UpdateResourceResponse> {
    return this.http.put<UpdateResourceResponse>(`${environment.apiBaseUrl}/resources/${id}`, request, {
      context: skipErrorToast(),
    });
  }

  // POST /resources/{id}/archive, deliberately not DELETE /resources/{id} —
  // CLAUDE.md §4.5 deletes nothing, and a DELETE that silently meant "archive,
  // irreversibly" would invite a client to assume the row was gone. The empty
  // body is the whole payload; the transition takes no arguments.
  //
  // **Idempotent**, unusually for a write in this app: the handler returns early
  // when the resource is already archived and deliberately does not move
  // UpdatedAtUtc, so a repeat after a dropped response is safe.
  archive(id: string): Observable<ArchiveResourceResponse> {
    return this.http.post<ArchiveResourceResponse>(
      `${environment.apiBaseUrl}/resources/${id}/archive`,
      {},
      { context: skipErrorToast() },
    );
  }

  // PUT /resources/{id}/approvers (admin console phase 5). Replace-the-set,
  // same shape as the schedule next door and for the same reason: swapping one
  // approver for another in a single request has no intermediate state.
  replaceApprovers(id: string, approverUserIds: string[]): Observable<ReplaceApproversResponse> {
    return this.http.put<ReplaceApproversResponse>(
      `${environment.apiBaseUrl}/resources/${id}/approvers`,
      { approverUserIds },
      { context: skipErrorToast() },
    );
  }

  // PUT /resources/{id}/availability-windows (admin console phase 4).
  //
  // **Replace-the-set**: the whole weekly schedule goes in one request and an
  // omitted window is a deleted one. Its own endpoint rather than a field on
  // PUT /resources/{id} precisely so a rename cannot wipe a schedule by
  // omission — see ResourcesController.
  //
  // An empty array is a legitimate request meaning "this resource opens at no
  // time at all", which is why the caller must state it rather than achieve it
  // by leaving the field out.
  replaceAvailabilityWindows(
    id: string,
    windows: AvailabilityWindowInput[],
  ): Observable<ReplaceAvailabilityWindowsResponse> {
    return this.http.put<ReplaceAvailabilityWindowsResponse>(
      `${environment.apiBaseUrl}/resources/${id}/availability-windows`,
      { windows },
      { context: skipErrorToast() },
    );
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
  if (params.search !== undefined) {
    httpParams = httpParams.set('search', params.search);
  }
  if (params.requiresApproval !== undefined) {
    httpParams = httpParams.set('requiresApproval', params.requiresApproval);
  }

  return httpParams;
}
