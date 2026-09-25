import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { ActivationService } from '../services/activation.service';
import { SKIP_ERROR_TOAST } from '../../../core/http/skip-error-toast';

// User management phase 8's coverage sweep. `activate.component.spec.ts`
// already pins this service's exact wire shape (method, URL, body) through its
// own HTTP mock, but nothing anywhere asserted the one thing that shape does
// not show: that this call opts out of the global error toast. That matters
// more here than on most calls in this app — the activate screen renders its
// own inline refusal precisely so it never says more than the server did
// (CLAUDE.md §4.2's `0018` reasoning, inherited), and a global toast firing
// alongside it would be exactly the leak that screen exists to avoid. The same
// shape of gap admin console phase 7's sweep found in `users.service.ts`:
// enumerate files, do not scan names.

const API = 'http://localhost:5270';

describe('ActivationService', () => {
  let service: ActivationService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ActivationService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('posts the token and password to the activate endpoint', async () => {
    const result = firstValueFrom(service.activate('tok-123', 'a-good-long-password'));

    const req = httpMock.expectOne(`${API}/auth/activate`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ token: 'tok-123', password: 'a-good-long-password' });

    req.flush(null, { status: 204, statusText: 'No Content' });
    expect(await result).toBeNull();
  });

  // The load-bearing assertion this file exists for. A global toast on top of
  // the screen's own inline refusal would say more than the server did — the
  // one thing decision `0018`'s reasoning forbids for an indistinguishable
  // refusal like `InvalidActivationToken`.
  it('opts out of the global error toast', () => {
    firstValueFrom(service.activate('tok-123', 'a-good-long-password')).catch(() => undefined);

    const req = httpMock.expectOne(`${API}/auth/activate`);
    expect(req.request.context.get(SKIP_ERROR_TOAST)).toBe(true);

    req.flush(null, { status: 401, statusText: 'Unauthorized' });
  });
});
