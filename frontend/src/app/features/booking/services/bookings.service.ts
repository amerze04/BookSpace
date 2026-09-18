import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../../environments/environment';
import { PagedResult } from '../../../core/http/paged-result';
import { skipErrorToast } from '../../../core/http/skip-error-toast';
import {
  BookingDetail,
  BookingSummary,
  CancelBookingRequest,
  CancelBookingResponse,
  CreateBookingRequest,
  CreateBookingResponse,
  ListBookingsParams,
} from '../models/booking.models';

// Thin wrapper over POST /bookings — the same "bind and dispatch" thinness
// ResourcesService and AvailabilityService keep to: no retry policy, no
// mapping, no caching here, so the request shape lives in exactly one place.
//
// Skips the global error toast: the booking screen renders every rejection
// reason itself, in the right place (a top-of-form conflict, a field-level
// duration error — wp7-plan.md Phase 3 step 5), so a generic toast on top
// would be noise over the one failure this app explains well. Same reasoning
// ResourcesService, AvailabilityService and AuthService.login already apply.
//
// **No retry, here or anywhere above this method, and that is a deliberate
// constraint rather than an omission.** POST /bookings has no idempotency
// support of any kind — no header, no operation record — so a request whose
// response is lost in transit cannot be safely repeated: a second attempt
// creates a second booking, and dbo.CreateBooking is right to allow it, since
// two distinct bookings of the same slot on a pooled resource are legitimate.
// The gap is recorded against a future backend package in wp7-plan.md §7; the
// client's part of the mitigation is that nothing retries this call
// automatically and a transport failure tells the member their booking *may*
// have been created (step 5), rather than offering a button that could double
// it. Contrast RecurrenceRulesService.create, which does have a key.
@Injectable({ providedIn: 'root' })
export class BookingsService {
  private readonly http = inject(HttpClient);

  create(request: CreateBookingRequest): Observable<CreateBookingResponse> {
    return this.http.post<CreateBookingResponse>(`${environment.apiBaseUrl}/bookings`, request, {
      context: skipErrorToast(),
    });
  }

  // FR-4.4, the "view" half. The caller's own bookings — this client sends no
  // `scope` or `userId` at all (see ListBookingsParams), so the endpoint's own
  // default of BookingScope.Own applies.
  list(params: ListBookingsParams = {}): Observable<PagedResult<BookingSummary>> {
    return this.http.get<PagedResult<BookingSummary>>(`${environment.apiBaseUrl}/bookings`, {
      params: buildListParams(params),
      context: skipErrorToast(),
    });
  }

  getById(id: string): Observable<BookingDetail> {
    return this.http.get<BookingDetail>(`${environment.apiBaseUrl}/bookings/${id}`, {
      context: skipErrorToast(),
    });
  }

  // FR-4.4 and decision 0002, the "cancel" half.
  //
  // **Nothing retries this, and that is a harder rule here than on create.**
  // POST /bookings cannot be retried because it has no idempotency key (§7's
  // gap — a repeat would create a second booking). This one cannot be retried
  // for a different and more definite reason: it is *deliberately* not
  // idempotent, so a second call either returns 422 BookingNotCancellable or,
  // where the first had not yet committed, quietly rewrites CancelledByUserId,
  // CancelledAtUtc and the reason with a second actor's. Neither outcome is
  // something a retry button should be offering, so an unknown outcome is
  // reported as unknown and the member is sent to re-read the booking.
  //
  // The body is optional in full server-side; this client always sends one
  // (with `reason: null` when there is nothing to say) rather than sometimes
  // omitting it, so there is a single request shape to test and reason about.
  cancel(id: string, request: CancelBookingRequest): Observable<CancelBookingResponse> {
    return this.http.post<CancelBookingResponse>(
      `${environment.apiBaseUrl}/bookings/${id}/cancel`,
      request,
      { context: skipErrorToast() },
    );
  }
}

// A field is only added when the caller actually set it, so an omitted filter
// is genuinely absent from the URL rather than sent as a default this file
// invented — the backend's own documented defaults then apply, and there is one
// copy of them, not two that could disagree (decision 0015; the same rule
// ResourcesService.buildListParams follows).
//
// `page`/`pageSize` are checked against `undefined` rather than truthiness for
// the usual reason, and `status`/`resourceId` for a sharper one: an empty
// string would serialize as `?status=`, which model-binds to a null enum and so
// silently widens the query rather than failing.
function buildListParams(params: ListBookingsParams): HttpParams {
  let httpParams = new HttpParams();

  if (params.from !== undefined) {
    httpParams = httpParams.set('from', params.from);
  }
  if (params.to !== undefined) {
    httpParams = httpParams.set('to', params.to);
  }
  if (params.status !== undefined) {
    httpParams = httpParams.set('status', params.status);
  }
  if (params.resourceId !== undefined) {
    httpParams = httpParams.set('resourceId', params.resourceId);
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
