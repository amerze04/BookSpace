import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { ActivateComponent } from '../components/activate/activate.component';

// The activation screen — the one an invited colleague reaches from their email,
// and the only screen in this application somebody uses before they have an
// account.
//
// Two groups of assertions carry weight. The **refusal** must say the same
// thing however the token failed, because the server deliberately does
// (`401 InvalidActivationToken` covers expired, used, unknown and deactivated —
// plan §4.2, inheriting decision `0018`). And the **submit wiring** is driven
// through the rendered DOM, because the invite form shipped with a dead
// `(ngSubmit)` two days ago and every test that called `submit()` directly
// missed it.

const API = 'http://localhost:5270';

type TestableActivate = ActivateComponent & {
  password: () => string;
  confirmPassword: () => string;
  submitting: () => boolean;
  errorMessage: () => string | null;
  tokenMissing: boolean;
  passwordError: () => string | null;
  confirmError: () => string | null;
  canSubmit: () => boolean;
  markTouched(field: 'password' | 'confirmPassword'): void;
  onPasswordInput(event: Event): void;
  onConfirmInput(event: Event): void;
  submit(): void;
};

function fakeInputEvent(value: string): Event {
  return { target: { value } } as unknown as Event;
}

describe('ActivateComponent', () => {
  let httpMock: HttpTestingController;
  let navigate: ReturnType<typeof vi.fn>;

  function createFixture(token: string | null = 'a-real-token') {
    navigate = vi.fn().mockResolvedValue(true);

    TestBed.configureTestingModule({
      imports: [ActivateComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate } },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { queryParamMap: convertToParamMap(token === null ? {} : { token }) },
          },
        },
      ],
    });

    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(ActivateComponent);
    fixture.detectChanges();
    return { fixture, component: fixture.componentInstance as TestableActivate };
  }

  afterEach(() => {
    try {
      httpMock.verify();
    } finally {
      TestBed.resetTestingModule();
    }
  });

  function fillMatching(component: TestableActivate, password = 'a-good-long-password'): void {
    component.onPasswordInput(fakeInputEvent(password));
    component.onConfirmInput(fakeInputEvent(password));
  }

  // ---- The token ----

  // The link's whole payload. Read from the URL and sent in the body — never
  // stored, never logged.
  it('sends the token from the query string with the chosen password', () => {
    const { component } = createFixture('tok-123');
    fillMatching(component, 'a-good-long-password');

    component.submit();

    const request = httpMock.expectOne(`${API}/auth/activate`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-123', password: 'a-good-long-password' });
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  // A link that lost its query string on the way through a mail client. Its own
  // state, because no control on this page could fix it — and asking for a
  // password would produce a guaranteed 400.
  it('says the link is incomplete when there is no token at all', () => {
    const { fixture, component } = createFixture(null);

    expect(component.tokenMissing).toBe(true);
    expect(fixture.nativeElement.querySelector('form')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('incomplete');
  });

  // ---- The password rules ----

  it('mirrors the server’s minimum before spending a round trip on it', () => {
    const { component } = createFixture();

    component.onPasswordInput(fakeInputEvent('short'));
    component.markTouched('password');

    expect(component.passwordError()).toContain('at least 12');
    expect(component.canSubmit()).toBe(false);
  });

  it('accepts a password exactly at the minimum', () => {
    const { component } = createFixture();
    fillMatching(component, 'a'.repeat(12));

    expect(component.passwordError()).toBeNull();
    expect(component.canSubmit()).toBe(true);
  });

  // No composition rules, matching the server — length only, following NIST SP
  // 800-63B. Asserted so a later "add a symbol" would be a deliberate change.
  it('accepts a long password with no character variety', () => {
    const { component } = createFixture();
    fillMatching(component, 'correct horse battery staple');

    expect(component.canSubmit()).toBe(true);
  });

  // There is no password reset in this application (plan §3.4), so a typo here
  // is permanent — which is the whole reason a confirm field exists at all,
  // since the server only ever receives one password.
  it('will not submit until both passwords match', () => {
    const { component } = createFixture();

    component.onPasswordInput(fakeInputEvent('a-good-long-password'));
    component.onConfirmInput(fakeInputEvent('a-good-long-passwordd'));
    component.markTouched('confirmPassword');

    expect(component.confirmError()).toContain('must match');
    expect(component.canSubmit()).toBe(false);
  });

  it('says nothing about a field the person has not left yet', () => {
    const { component } = createFixture();

    component.onPasswordInput(fakeInputEvent('sh'));

    expect(component.passwordError()).toBeNull();
  });

  // ---- The submit wiring ----

  // **Regression in spirit, from the invite form's dead `(ngSubmit)`.** Driven
  // by clicking the real button, because that is the only level at which that
  // class of bug exists.
  it('submits when the button is clicked', () => {
    const { fixture, component } = createFixture('tok-123');
    fillMatching(component);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement).click();

    httpMock.expectOne(`${API}/auth/activate`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('prevents the browser from submitting the form itself', () => {
    const { fixture, component } = createFixture();
    fillMatching(component);
    fixture.detectChanges();

    const event = new Event('submit', { bubbles: true, cancelable: true });
    (fixture.nativeElement.querySelector('form') as HTMLFormElement).dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    httpMock.expectOne(`${API}/auth/activate`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('disables the submit while the request is in flight', () => {
    const { component } = createFixture();
    fillMatching(component);

    component.submit();
    expect(component.submitting()).toBe(true);
    expect(component.canSubmit()).toBe(false);

    httpMock.expectOne(`${API}/auth/activate`).flush(null, { status: 204, statusText: 'No Content' });
    expect(component.submitting()).toBe(false);
  });

  // ---- Success ----

  // 204, not a session. Sending them to login keeps session minting in the one
  // handler that owns FR-2.4's active-user and suspended-org checks.
  it('sends them to login with a flag, rather than signing them in', () => {
    const { component } = createFixture();
    fillMatching(component);
    component.submit();

    httpMock.expectOne(`${API}/auth/activate`).flush(null, { status: 204, statusText: 'No Content' });

    expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { activated: 1 } });
  });

  // ---- Refusal ----

  // **The property this screen exists to preserve.** The server answers 401
  // identically whether the link expired, was already used, never existed, or
  // belongs to a deactivated account — so that it cannot be used to discover
  // which invitations are outstanding. Saying more here would give away exactly
  // what it withheld.
  it('says the same thing for every way a token can be refused', () => {
    const messages = new Set<string>();

    for (const body of [
      { status: 401, title: 'Authentication failed.', reasonCode: 'InvalidActivationToken' },
      // The server cannot distinguish these, but a future one might try —
      // whatever arrives, this screen must not elaborate.
      { status: 401, title: 'Authentication failed.', reasonCode: 'InvalidActivationToken' },
      { status: 401, title: 'Authentication failed.', reasonCode: 'SomethingElseEntirely' },
    ]) {
      const { component } = createFixture();
      fillMatching(component);
      component.submit();
      httpMock.expectOne(`${API}/auth/activate`).flush(body, { status: 401, statusText: 'Unauthorized' });
      messages.add(component.errorMessage() ?? '');
      httpMock.verify();
      TestBed.resetTestingModule();
    }

    expect(messages.size).toBe(1);
    const [message] = [...messages];
    expect(message.toLowerCase()).not.toContain('expired link');
    expect(message.toLowerCase()).not.toContain('already used this');
    expect(message.toLowerCase()).not.toContain('no such');
  });

  // It still has to be actionable: there is no way to re-issue an invitation
  // (plan §6), so asking an administrator is genuinely the only route left.
  it('tells them what to do instead', () => {
    const { component } = createFixture();
    fillMatching(component);
    component.submit();

    httpMock
      .expectOne(`${API}/auth/activate`)
      .flush(
        { status: 401, title: 'Authentication failed.', reasonCode: 'InvalidActivationToken' },
        { status: 401, statusText: 'Unauthorized' },
      );

    expect(component.errorMessage()).toContain('ask your administrator');
    expect(navigate).not.toHaveBeenCalled();
  });

  it.each([
    ['a request that reached no server', 0, 'Unable to reach the server'],
    ['a throttled caller', 429, 'Too many attempts'],
    ['a server failure', 500, 'our end'],
  ])('reports %s distinctly from a bad token', (_label, status, expected) => {
    const { component } = createFixture();
    fillMatching(component);
    component.submit();

    httpMock.expectOne(`${API}/auth/activate`).flush(null, { status, statusText: 'Error' });

    expect(component.errorMessage()).toContain(expected);
  });

  it('clears the refusal once the password is edited', () => {
    const { component } = createFixture();
    fillMatching(component);
    component.submit();
    httpMock
      .expectOne(`${API}/auth/activate`)
      .flush(
        { status: 401, title: 'Authentication failed.', reasonCode: 'InvalidActivationToken' },
        { status: 401, statusText: 'Unauthorized' },
      );
    expect(component.errorMessage()).not.toBeNull();

    component.onPasswordInput(fakeInputEvent('a-different-long-password'));

    expect(component.errorMessage()).toBeNull();
  });
});
