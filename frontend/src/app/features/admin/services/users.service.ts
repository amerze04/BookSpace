import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { PagedResult } from '../../../core/http/paged-result';
import { skipErrorToast } from '../../../core/http/skip-error-toast';
import {
  CreateUserRequest,
  CreatedUser,
  DirectoryUser,
  EligibleUser,
  ListUsersParams,
  ReplaceUserRolesRequest,
  UserDetail,
  UserWriteResult,
} from '../models/users.models';

// The only consumer of `GET /users`, which admin console phase 1 added for
// exactly this screen. It lives under `features/admin/` rather than beside
// `ResourcesService` because unlike `/resources` this endpoint has no
// member-facing use at all — it is TenantAdmin-only end to end.
//
// Skips the global error toast, like every other call the admin console makes:
// the approvers screen renders its own inline state for a failed load, and a
// toast on top would be noise over the one explanation that helps.
@Injectable({ providedIn: 'root' })
export class UsersService {
  private readonly http = inject(HttpClient);

  // Returns the decision `0018` eligible-approver set for the caller's tenant.
  //
  // It sends no `scope`, which is why this method needed no change when user
  // management phase 4 widened the endpoint: an omitted scope still means the
  // eligible set, byte for byte. That was the point of making the wider set
  // opt-in rather than the default. The directory's `scope=All` mode belongs to
  // the screen phase 6 builds, and adding the parameter here before there is a
  // caller for it would only be a second way to get the picker's answer wrong.
  list(params: ListUsersParams = {}): Observable<PagedResult<EligibleUser>> {
    return this.http.get<PagedResult<EligibleUser>>(`${environment.apiBaseUrl}/users`, {
      params: buildParams(params),
      context: skipErrorToast(),
    });
  }

  // Every user in the tenant — Members and deactivated accounts included.
  //
  // **A separate method rather than a `scope` argument on `list`**, even though
  // it is one endpoint on the server. The two answers are different row types
  // (`DirectoryUser` carries `isActive`), and a single method would have to
  // return the wider one to both callers — which would let the picker read a
  // field that happens to be true of every row it sees, and would put the scope
  // that must never be sent by accident one optional argument away from the
  // call that must never send it.
  listDirectory(params: Omit<ListUsersParams, 'scope'> = {}): Observable<PagedResult<DirectoryUser>> {
    return this.http.get<PagedResult<DirectoryUser>>(`${environment.apiBaseUrl}/users`, {
      params: buildParams({ ...params, scope: 'All' }),
      context: skipErrorToast(),
    });
  }

  // Provisions a colleague and sends them an invitation.
  //
  // The response carries a live activation link (see `CreatedUser`), so nothing
  // here logs it, caches it, or hands it anywhere but the caller.
  create(request: CreateUserRequest): Observable<CreatedUser> {
    return this.http.post<CreatedUser>(`${environment.apiBaseUrl}/users`, request, {
      context: skipErrorToast(),
    });
  }

  // The user detail screen's load, phase 7. A deactivated user is still
  // readable by id — only the directory's default *view* hides one, and this
  // is not that.
  getById(id: string): Observable<UserDetail> {
    return this.http.get<UserDetail>(`${environment.apiBaseUrl}/users/${id}`, {
      context: skipErrorToast(),
    });
  }

  // FR-2.4. Idempotent on the server — deactivating an already-inactive user
  // returns the current state and writes nothing — so this is safe to call
  // without a client-side guard against a double click.
  deactivate(id: string): Observable<UserWriteResult> {
    return this.http.post<UserWriteResult>(`${environment.apiBaseUrl}/users/${id}/deactivate`, null, {
      context: skipErrorToast(),
    });
  }

  // The reverse, and also idempotent. Unlike archiving a resource, this state
  // is meant to be reversed.
  reactivate(id: string): Observable<UserWriteResult> {
    return this.http.post<UserWriteResult>(`${environment.apiBaseUrl}/users/${id}/reactivate`, null, {
      context: skipErrorToast(),
    });
  }

  // FR-1.5. Replace-the-set, matching `ReplaceApprovers` — the whole role list,
  // never a delta.
  replaceRoles(id: string, request: ReplaceUserRolesRequest): Observable<UserWriteResult> {
    return this.http.put<UserWriteResult>(`${environment.apiBaseUrl}/users/${id}/roles`, request, {
      context: skipErrorToast(),
    });
  }
}

// A field is only sent when the caller actually set it, so the backend's own
// documented default applies instead of a second copy of it living here.
function buildParams(params: ListUsersParams): HttpParams {
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
  if (params.search !== undefined) {
    httpParams = httpParams.set('search', params.search);
  }
  // Only ever set by listDirectory. An omitted scope is the eligible-approver
  // set, which is what every other caller wants and what the backend defaults
  // to — see UserScope for why the narrow one is the default.
  if (params.scope !== undefined) {
    httpParams = httpParams.set('scope', params.scope);
  }

  return httpParams;
}
