import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { PagedResult } from '../../../core/http/paged-result';
import { skipErrorToast } from '../../../core/http/skip-error-toast';
import { EligibleUser, ListUsersParams } from '../models/users.models';

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
  // There is no "all users" mode and no parameter that would produce one.
  list(params: ListUsersParams = {}): Observable<PagedResult<EligibleUser>> {
    return this.http.get<PagedResult<EligibleUser>>(`${environment.apiBaseUrl}/users`, {
      params: buildParams(params),
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

  return httpParams;
}
