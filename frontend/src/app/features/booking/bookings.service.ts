import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { skipErrorToast } from '../../core/http/skip-error-toast';
import { CreateBookingRequest, CreateBookingResponse } from './booking.models';

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
}
